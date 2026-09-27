using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Captr.Core.Supervision;

/// <summary>
/// A SHA-256 of a Windows executable that is the same before and after it is
/// Authenticode-signed. Owns the one fact that lets Captr pin the exact upstream
/// FFmpeg AND ship it signed: the digest recorded when the pinned download was
/// verified still identifies the binary after the release build signs it.
/// </summary>
/// <remarks>
/// <para>
/// Signing changes exactly three things in a PE file: it appends the certificate
/// table (after padding the file to an 8-byte boundary with zeros), points the
/// optional header's security directory entry at it, and rewrites the header
/// checksum. This digest covers every other byte: the file up to the certificate
/// table (or its end, if unsigned), with the checksum and the security directory
/// entry read as zeros, padded with zeros to a multiple of 8 bytes. Any other change
/// to the file — a single flipped bit in code, data, or resources — changes it.
/// </para>
/// <para>
/// <c>build/fetch-ffmpeg.ps1</c> computes the same digest in PowerShell
/// (<c>Get-PeImageDigest</c>); <c>PeImageDigestTests</c> holds the two to each other.
/// </para>
/// </remarks>
public static class PeImageDigest
{
    /// <summary>Lower-case hex SHA-256, or throws <see cref="InvalidDataException"/>
    /// when the file is not a PE image.</summary>
    public static string Compute(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long length = file.Length;

        Span<byte> header = stackalloc byte[4096];
        int headerLength = file.Read(header);
        header = header[..headerLength];
        if (headerLength < 0x40 || header[0] != 'M' || header[1] != 'Z')
        {
            throw new InvalidDataException($"{path} is not a Windows executable.");
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        int optionalHeader = peOffset + 24;
        if (peOffset < 0 || optionalHeader + 2 > headerLength
            || BinaryPrimitives.ReadUInt32LittleEndian(header[peOffset..]) != 0x00004550)
        {
            throw new InvalidDataException($"{path} has no PE header.");
        }

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header[optionalHeader..]);
        int checksumOffset = optionalHeader + 64;
        int dataDirectories = optionalHeader + (magic == 0x20B ? 112 : 96);
        int securityEntry = dataDirectories + (4 * 8);
        if (securityEntry + 8 > headerLength)
        {
            throw new InvalidDataException($"{path} has a truncated PE header.");
        }

        uint certificateOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[securityEntry..]);
        uint certificateSize = BinaryPrimitives.ReadUInt32LittleEndian(header[(securityEntry + 4)..]);
        long imageEnd = certificateSize > 0 && certificateOffset > 0 && certificateOffset <= length
            ? certificateOffset
            : length;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        file.Position = 0;
        byte[] buffer = new byte[1 << 20];
        long position = 0;
        while (position < imageEnd)
        {
            int wanted = (int)Math.Min(buffer.Length, imageEnd - position);
            int read = file.Read(buffer, 0, wanted);
            if (read == 0)
            {
                break;
            }

            ZeroIfInside(buffer, position, read, checksumOffset, 4);
            ZeroIfInside(buffer, position, read, securityEntry, 8);
            sha.AppendData(buffer, 0, read);
            position += read;
        }

        int padding = (int)((8 - (imageEnd % 8)) % 8);
        if (padding > 0)
        {
            sha.AppendData(new byte[padding]);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Zeroes the part of [fieldOffset, fieldOffset+fieldLength) that falls
    /// inside this buffer, which holds the file's bytes from bufferStart onwards.</summary>
    private static void ZeroIfInside(byte[] buffer, long bufferStart, int count, long fieldOffset, int fieldLength)
    {
        long from = Math.Max(fieldOffset, bufferStart);
        long to = Math.Min(fieldOffset + fieldLength, bufferStart + count);
        if (from < to)
        {
            Array.Clear(buffer, (int)(from - bufferStart), (int)(to - from));
        }
    }
}
