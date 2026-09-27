<#
.SYNOPSIS
    Builds the signed Captr installer with Inno Setup (SPEC §11) plus checksums and
    the machine-readable release record.

.DESCRIPTION
    Steps:
      1. Ensure the published payload exists (build.ps1 -Publish creates publish/).
      2. Ensure Inno Setup — a PINNED version, checksum-verified, installed
         per-user under tools/innosetup (no admin needed, reproducible).
      3. Generate version.iss from Version.props (the single version source).
      4. Compile build/installer/captr.iss. When a signing method is configured,
         Inno Setup calls build/Sign-Artifacts.ps1 to sign BOTH the installer and
         the uninstaller it embeds (SignTool + SignedUninstaller in captr.iss).
      5. Verify every shipped file's signature (build/Verify-Signatures.ps1): the
         published payload and the installer. Any unsigned, untimestamped, or
         wrongly-signed file fails the build. Skipped, loudly, for unsigned builds.
      6. Emit artifacts/SHA256SUMS.txt and artifacts/release.json (version, commit,
         .NET SDK and runtime, FFmpeg build id, file hashes, whether VERIFIED signed
         - SPEC §11), and the archived symbols as captr-symbols-<version>.zip.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent),
    # Fail instead of warning when no signing method is configured (releases).
    [switch]$RequireSigning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib\Signing.ps1')

# Pinned Inno Setup (zlib-style licence, explicitly free for commercial use, SPEC §2).
$innoVersion = '6.7.3'
$innoUrl = "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-$innoVersion.exe"
$innoSha256 = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'

$publishDir = Join-Path $RepoRoot 'publish'
$artifactsDir = Join-Path $RepoRoot 'artifacts'
$toolsInno = Join-Path $RepoRoot 'tools\innosetup'
$iscc = Join-Path $toolsInno 'ISCC.exe'

if (-not (Test-Path (Join-Path $publishDir 'Captr.App.exe'))) {
    throw "No published payload at $publishDir. Run: pwsh build/build.ps1 -Publish"
}

# The payload must be the version we are about to stamp on the installer.
#
# This script packages whatever is already in publish/; it does not build. So
# changing Version.props and running only this produced an installer that ANNOUNCED
# the new version, registered it in Programs and Features, and contained binaries
# reporting the old one. Everything downstream then disagreed: the installer's
# upgrade check, `captr version`, and the About page. Caught here rather than
# discovered by a user.
[xml]$versionPropsCheck = Get-Content (Join-Path $RepoRoot 'Version.props')
$expectedVersion = $versionPropsCheck.Project.PropertyGroup.CaptrVersion.Trim()
$publishedVersion = (Get-Item (Join-Path $publishDir 'Captr.App.exe')).VersionInfo.ProductVersion

# The published version carries "+<commit>"; compare only the version part.
if ($publishedVersion) { $publishedVersion = ($publishedVersion -split '\+')[0] }

if ($publishedVersion -ne $expectedVersion) {
    throw ("The published payload is version '$publishedVersion' but Version.props says " +
           "'$expectedVersion'. Re-publish before packaging: pwsh build/build.ps1 -Publish")
}

# ...and it must be a payload built from the CODE THAT IS HERE NOW.
#
# The version check above is not enough on its own, and that is not a hypothetical:
# during development the version stays at 0.1.0 for weeks, so a publish/ folder from
# yesterday matches Version.props perfectly and packages happily. The result is an
# installer that installs, runs, and shows none of the day's work - with nothing
# anywhere saying why.
#
# So compare the payload against the source it was supposedly built from. Anything
# under src/ that is newer than the published binaries means the payload is stale.
$publishedStamp = (Get-Item (Join-Path $publishDir 'Captr.App.dll')).LastWriteTimeUtc

$newerSource = Get-ChildItem (Join-Path $RepoRoot 'src') -Recurse -File `
    -Include *.cs, *.xaml, *.csproj, *.props, *.resx, *.ico |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.LastWriteTimeUtc -gt $publishedStamp } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 3

if ($newerSource) {
    $names = ($newerSource | ForEach-Object { '  ' + $_.FullName.Substring($RepoRoot.Length + 1) }) -join "`n"
    throw ("The published payload in $publishDir was built at " +
           "$($publishedStamp.ToLocalTime().ToString('yyyy-MM-dd HH:mm')) and these source files " +
           "have changed since:`n$names`n`n" +
           "Packaging it would produce an installer without those changes. Re-publish first:`n" +
           "  pwsh build/build.ps1 -Publish`n" +
           "or do the whole thing in one step:`n" +
           "  pwsh build/build.ps1 -Installer")
}

# --- 2. Inno Setup ---------------------------------------------------------------
if (-not (Test-Path $iscc)) {
    $installer = Join-Path $env:TEMP "innosetup-$innoVersion.exe"
    if (-not (Test-Path $installer) -or (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant() -ne $innoSha256) {
        Write-Host "Downloading Inno Setup $innoVersion"
        Invoke-WebRequest $innoUrl -OutFile $installer -UseBasicParsing
    }

    $actual = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $innoSha256) {
        Remove-Item $installer -Force
        throw "Inno Setup download checksum mismatch (expected $innoSha256, got $actual). Deleted; refusing to proceed."
    }

    Write-Host "Installing Inno Setup into $toolsInno (per-user, silent)"
    Start-Process $installer -ArgumentList "/VERYSILENT", "/CURRENTUSER", "/DIR=`"$toolsInno`"", "/NOICONS" -Wait
    if (-not (Test-Path $iscc)) { throw "Inno Setup installation did not produce $iscc." }
}

# --- 3. version.iss from the single version source --------------------------------
[xml]$versionProps = Get-Content (Join-Path $RepoRoot 'Version.props')
$version = $versionProps.Project.PropertyGroup.CaptrVersion.Trim()
# build.ps1 resolves the commit once and passes it down as SourceRevisionId; fall back
# to asking git only when this script is run on its own.
$commit = $env:SourceRevisionId
if (-not $commit) { $commit = (& git -C $RepoRoot rev-parse --short=12 HEAD 2>$null) }
if (-not $commit) { $commit = 'unknown' }
Set-Content (Join-Path $PSScriptRoot 'installer\version.iss') @"
; GENERATED by make-installer.ps1 from Version.props — do not edit.
#define CaptrVersion "$version"
#define CaptrCommit "$commit"
"@

# --- 4. Compile (and, when configured, sign the installer and uninstaller) ------
New-Item -ItemType Directory -Force $artifactsDir | Out-Null
$signing = Get-SigningConfig
if (-not $signing -and $RequireSigning) {
    throw 'Signing is required but no signing method is configured (docs/developer-guide/releasing.md).'
}

$isccArgs = @('/Qp', "/O$artifactsDir")
if ($signing) {
    if (-not $env:CAPTR_SIGN_PUBLISHER) {
        throw ("Signing is configured but CAPTR_SIGN_PUBLISHER is not: set it to the common name on Captr's " +
               'certificate, so the build can prove every file carries THAT signature.')
    }

    # Inno runs this for the installer and for the uninstaller it embeds, handing over
    # a temporary copy of each ($f, already quoted). $q is Inno's escape for a quote.
    $signer = (Join-Path $PSScriptRoot 'Sign-Artifacts.ps1')
    $isccArgs += "/Scaptrsign=pwsh.exe -NoProfile -ExecutionPolicy Bypass -File `$q$signer`$q -Force -Require -Path `$f"
    $isccArgs += '/DCaptrSign'
}

& $iscc @isccArgs (Join-Path $PSScriptRoot 'installer\captr.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
$setupExe = Join-Path $artifactsDir "captr-setup-$version.exe"
if (-not (Test-Path $setupExe)) { throw "Expected installer not found: $setupExe" }

# --- 5. Verify every shipped signature ------------------------------------------------
$signatureReport = Join-Path $artifactsDir 'signature-report.json'
Remove-Item $signatureReport -ErrorAction SilentlyContinue
$verified = $false
if ($signing) {
    & (Join-Path $PSScriptRoot 'Verify-Signatures.ps1') -Path $publishDir, $setupExe -ReportPath $signatureReport
    $verified = $true
} else {
    Write-Warning 'Signature verification skipped: this is an UNSIGNED build (release.json records "signed": false).'
}

# --- Symbols: archived with the release, never shipped -------------------------------
$symbolsDir = Join-Path $artifactsDir 'symbols'
$symbolsZip = Join-Path $artifactsDir "captr-symbols-$version.zip"
Remove-Item $symbolsZip -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $symbolsDir '*.pdb')) {
    Compress-Archive -Path (Join-Path $symbolsDir '*.pdb') -DestinationPath $symbolsZip
}

# --- 6. Checksums + release record ------------------------------------------------
$ffCaps = Get-Content (Join-Path $RepoRoot 'tools\ffmpeg\capabilities.json') -Raw | ConvertFrom-Json
$hashes = [ordered]@{}
foreach ($file in @($setupExe) + @(Get-Item $symbolsZip -ErrorAction SilentlyContinue | ForEach-Object FullName)) {
    $hashes[(Split-Path $file -Leaf)] = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
}

# The time the COMMIT was made, not the time this machine packaged it: two builds of
# the same commit produce the same record.
$commitTime = (& git -C $RepoRoot log -1 --format=%cI HEAD 2>$null)
$builtAt = if ($commitTime) { ([DateTimeOffset]::Parse($commitTime)).ToUniversalTime().ToString('o') } else { (Get-Date).ToUniversalTime().ToString('o') }
$runtimeVersion = (Get-Content (Join-Path $publishDir 'Captr.App.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks |
    Where-Object name -eq 'Microsoft.NETCore.App' | Select-Object -ExpandProperty version

($hashes.GetEnumerator() | ForEach-Object { "$($_.Value)  $($_.Key)" }) |
    Set-Content (Join-Path $artifactsDir 'SHA256SUMS.txt') -Encoding ascii

[ordered]@{
    product        = 'Captr'
    version        = $version
    commit         = $commit
    builtAtUtc     = $builtAt
    dotnetVersion  = (& dotnet --version).Trim()
    runtimeVersion = $runtimeVersion
    ffmpegBuildId  = $ffCaps.buildId
    ffmpegSha256   = $ffCaps.sha256
    files          = $hashes
    # True only when Verify-Signatures passed over every shipped file - not merely
    # because a signing variable happened to be set.
    signed         = $verified
    signedBy       = $(if ($verified) { $env:CAPTR_SIGN_PUBLISHER } else { $null })
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $artifactsDir 'release.json') -Encoding utf8

Write-Host ""
Write-Host "Installer: $setupExe"
Write-Host "Release record: $(Join-Path $artifactsDir 'release.json')"
