using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

using Captr.Core.Supervision;

using Shouldly;

namespace Captr.Core.Tests.Supervision;

/// <summary>
/// Captr runs only the pinned FFmpeg, found only where Captr put it, and proves it
/// is that build before running it.
/// </summary>
public sealed class FfmpegIntegrityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-ffmpeg-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Any real PE file will do as a stand-in for ffmpeg.exe: this test
    /// run's own executable.</summary>
    private static string SomeExecutable() => Environment.ProcessPath!;

    private string InstallLayoutWith(string executable, string? recordedDigest)
    {
        string ffmpegFolder = Path.Combine(_dir, "install", "ffmpeg");
        Directory.CreateDirectory(ffmpegFolder);
        string target = Path.Combine(ffmpegFolder, "ffmpeg.exe");
        File.Copy(executable, target, overwrite: true);

        var capabilities = new Dictionary<string, object> { ["buildId"] = "test" };
        if (recordedDigest is not null)
        {
            capabilities["binaryDigests"] = new Dictionary<string, string> { ["ffmpeg.exe"] = recordedDigest };
        }

        File.WriteAllText(Path.Combine(ffmpegFolder, "capabilities.json"), JsonSerializer.Serialize(capabilities));
        return Path.Combine(_dir, "install");
    }

    [Fact]
    public void An_installed_copy_never_looks_outside_its_own_folder_for_ffmpeg()
    {
        // From C:\Program Files\Captr\ the development search used to walk up to
        // C:\tools\ffmpeg\bin\ffmpeg.exe — a folder any standard user may create.
        string[] candidates = [.. FfmpegLocator.CandidatePaths(@"C:\Program Files\Captr\", "ffmpeg.exe")];

        candidates.ShouldBe([@"C:\Program Files\Captr\ffmpeg\ffmpeg.exe"]);
    }

    [Fact]
    public void A_development_build_looks_only_inside_its_own_checkout()
    {
        string checkout = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(checkout, "Captr.slnx")))
        {
            checkout = Path.GetDirectoryName(checkout.TrimEnd('\\'))!;
        }

        string[] candidates = [.. FfmpegLocator.CandidatePaths(AppContext.BaseDirectory, "ffmpeg.exe")];

        candidates.ShouldAllBe(path => path.StartsWith(checkout, StringComparison.OrdinalIgnoreCase));
        candidates.ShouldContain(Path.Combine(checkout, "tools", "ffmpeg", "bin", "ffmpeg.exe"));
    }

    [Fact]
    public void The_pinned_binary_is_accepted()
    {
        string install = InstallLayoutWith(SomeExecutable(), PeImageDigest.Compute(SomeExecutable()));

        FfmpegLocator.Find(install, "ffmpeg.exe").ShouldBe(Path.Combine(install, "ffmpeg", "ffmpeg.exe"));
    }

    [Fact]
    public void A_binary_changed_by_one_bit_is_refused()
    {
        string install = InstallLayoutWith(SomeExecutable(), PeImageDigest.Compute(SomeExecutable()));
        string ffmpeg = Path.Combine(install, "ffmpeg", "ffmpeg.exe");
        byte[] bytes = File.ReadAllBytes(ffmpeg);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(ffmpeg, bytes);

        Should.Throw<FfmpegIntegrityException>(() => FfmpegLocator.Find(install, "ffmpeg.exe"))
            .Message.ShouldContain("Reinstall Captr");
    }

    [Fact]
    public void A_binary_with_no_record_to_check_against_is_refused()
    {
        string install = InstallLayoutWith(SomeExecutable(), recordedDigest: null);

        Should.Throw<FfmpegIntegrityException>(() => FfmpegLocator.Find(install, "ffmpeg.exe"));
    }

    [Fact]
    public void Signing_a_binary_does_not_change_its_digest_but_any_other_change_does()
    {
        // What Authenticode signing does to a PE file, done by hand: pad to 8 bytes
        // with zeros, append a certificate table, point the security directory at it,
        // and rewrite the header checksum. The release build signs the pinned FFmpeg
        // AFTER its digest is recorded, so the digest must survive exactly this.
        string original = Path.Combine(_dir, "original.exe");
        File.Copy(SomeExecutable(), original);
        byte[] bytes = File.ReadAllBytes(original);
        string before = PeImageDigest.Compute(original);

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        int optionalHeader = peOffset + 24;
        bool pe32Plus = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optionalHeader)) == 0x20B;
        int securityEntry = optionalHeader + (pe32Plus ? 112 : 96) + 32;

        int padded = (bytes.Length + 7) / 8 * 8;
        byte[] certificate = new byte[1200];
        Random.Shared.NextBytes(certificate);
        byte[] signed = new byte[padded + certificate.Length];
        bytes.CopyTo(signed, 0);
        certificate.CopyTo(signed, padded);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(securityEntry), (uint)padded);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(securityEntry + 4), (uint)certificate.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(optionalHeader + 64), 0xDEADBEEF);
        string signedPath = Path.Combine(_dir, "signed.exe");
        File.WriteAllBytes(signedPath, signed);

        PeImageDigest.Compute(signedPath).ShouldBe(before, "signing must not break the pin");

        signed[padded / 2] ^= 0x80;
        File.WriteAllBytes(signedPath, signed);
        PeImageDigest.Compute(signedPath).ShouldNotBe(before, "any change to the image itself must");
    }

    [Fact]
    public void Adoption_never_takes_a_process_that_is_not_the_bundled_ffmpeg()
    {
        // Recovery adopts (and then stops) the encoder a crashed host left running,
        // identified from the session journal - a file in the working folder. A
        // journal naming some other running program had THAT program killed.
        using var bystander = Process.Start(new ProcessStartInfo("ping", ["-n", "30", "127.0.0.1"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            string bystanderImage = bystander.MainModule!.FileName;

            FfmpegProcess? adopted = ProcessAdoption.TryAdopt(
                bystander.Id, bystander.StartTime.ToUniversalTime(), bystanderImage,
                bundledFfmpegPath: @"C:\Program Files\Captr\ffmpeg\ffmpeg.exe");

            adopted.ShouldBeNull();
            bystander.HasExited.ShouldBeFalse();
        }
        finally
        {
            bystander.Kill();
        }
    }
}
