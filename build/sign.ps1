<#
.SYNOPSIS
    Authenticode-signs Captr binaries and the installer (SPEC §11).

.DESCRIPTION
    Signs with SHA-256 and an RFC 3161 timestamp so signatures outlive the
    certificate. Driven entirely by environment variables — never commit a
    certificate, thumbprint, or password:

      CAPTR_SIGN_PFX            path to the .pfx certificate file
      CAPTR_SIGN_PFX_PASSWORD   password for the .pfx
      CAPTR_SIGN_TIMESTAMP_URL  RFC 3161 timestamp server (default: DigiCert)

    When CAPTR_SIGN_PFX is absent the script logs a PROMINENT warning and succeeds,
    so a developer without certificate access can always build (SPEC §11).

    Signs: Captr.exe, captr.exe, and the bundled ffmpeg.exe/ffprobe.exe. Signing the
    encoder binaries modifies them — this is the documented, permitted modification
    of the otherwise bit-for-bit-unmodified FFmpeg build (SPEC §2), and it matters:
    an unsigned well-known-name exe spawned by a signed parent while capturing the
    screen is a textbook endpoint-protection detection.
#>
[CmdletBinding()]
param(
    # A directory (all known binaries inside are signed) or a single file.
    [Parameter(Mandatory)] [string]$Target
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pfx = $env:CAPTR_SIGN_PFX
if (-not $pfx) {
    Write-Warning '=============================================================='
    Write-Warning ' UNSIGNED BUILD: CAPTR_SIGN_PFX is not set.'
    Write-Warning ' The binaries will NOT be Authenticode-signed. Fine for local'
    Write-Warning ' development; a release MUST be signed (SPEC §11).'
    Write-Warning '=============================================================='
    return
}
if (-not (Test-Path $pfx)) { throw "CAPTR_SIGN_PFX points to a missing file: $pfx" }

$tsUrl = if ($env:CAPTR_SIGN_TIMESTAMP_URL) { $env:CAPTR_SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }

# Locate signtool from the Windows SDK.
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { throw 'signtool.exe not found — install the Windows 10/11 SDK signing tools.' }

$files = if (Test-Path $Target -PathType Container) {
    Get-ChildItem $Target -Recurse -Include 'Captr.exe', 'captr.exe', 'ffmpeg.exe', 'ffprobe.exe' | Select-Object -ExpandProperty FullName
} else { @($Target) }

foreach ($f in $files) {
    Write-Host "Signing $f"
    & $signtool sign /fd SHA256 /f $pfx /p $env:CAPTR_SIGN_PFX_PASSWORD /tr $tsUrl /td SHA256 $f
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $f" }
}
Write-Host "Signed $($files.Count) file(s)."
