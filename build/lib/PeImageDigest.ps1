<#
.SYNOPSIS
    Get-PeImageDigest: a SHA-256 of a Windows executable that is the same before
    and after it is Authenticode-signed.

.DESCRIPTION
    The PowerShell twin of Captr.Core.Supervision.PeImageDigest - see that type for
    the full reasoning. In short: signing pads the file to 8 bytes, appends the
    certificate table, points the security directory at it, and rewrites the header
    checksum. This digest covers every other byte, with those two header fields read
    as zeros, zero-padded to a multiple of 8. So the digest recorded when the pinned
    FFmpeg download is verified still identifies the binary after release signing,
    and any other change to it - one flipped bit - does not match.

    Dot-source this file: . (Join-Path $PSScriptRoot 'lib\PeImageDigest.ps1')
#>
function Get-PeImageDigest {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "$Path is not a Windows executable."
    }

    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -lt 0 -or $pe + 26 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) {
        throw "$Path has no PE header."
    }

    $optionalHeader = $pe + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optionalHeader)
    $checksum = $optionalHeader + 64
    $security = $optionalHeader + $(if ($magic -eq 0x20B) { 112 } else { 96 }) + 32

    $certificateOffset = [BitConverter]::ToUInt32($bytes, $security)
    $certificateSize = [BitConverter]::ToUInt32($bytes, $security + 4)
    $end = if ($certificateSize -gt 0 -and $certificateOffset -gt 0 -and $certificateOffset -le $bytes.Length) {
        [int]$certificateOffset
    } else {
        $bytes.Length
    }

    [Array]::Clear($bytes, $checksum, 4)
    [Array]::Clear($bytes, $security, 8)

    $sha = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $sha.AppendData($bytes, 0, $end)
        $padding = (8 - ($end % 8)) % 8
        if ($padding -gt 0) { $sha.AppendData([byte[]]::new($padding)) }
        return [Convert]::ToHexString($sha.GetHashAndReset()).ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}
