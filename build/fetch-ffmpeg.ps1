<#
.SYNOPSIS
    Fetches the pinned FFmpeg build into tools/ffmpeg and verifies it.

.DESCRIPTION
    Reads build/ffmpeg.lock.json (the single source of truth for which FFmpeg we ship),
    downloads that exact release asset, verifies its SHA-256 against the committed
    checksum (hard failure on mismatch — SPEC §11), extracts ffmpeg.exe/ffprobe.exe,
    and then interrogates the binary to prove it actually has what Captr needs:

      * the ddagrab filter — FFmpeg's DXGI Desktop Duplication capture. Generic builds
        sometimes omit it, and without it Captr cannot capture at all (SPEC §11:
        "confirm the build actually contains the required capture filter").
      * every hardware encoder we probe for at runtime.
      * which software encoders are present (libx264 in the GPL build, libopenh264
        in both flavors) — this decides the software-encoder fallback tier. The
        answer is recorded, not assumed, so the runtime catalog only ever offers
        what the shipped binary actually contains.

    Results land in tools/ffmpeg/capabilities.json, which the application build embeds
    and the release record references (exact upstream build identifier, SPEC §2).

    Idempotent: if the archive is already present with a matching checksum, the
    download is skipped; verification always runs.

.NOTES
    LICENCE OBLIGATIONS (SPEC §2): the binary is shipped bit-for-bit unmodified,
    together with its licence texts (staged into tools/ffmpeg/licenses) and the
    written source offer in docs/ffmpeg-source-offer.md. If any modification ever
    becomes necessary (e.g. an Authenticode signature at release time), it must be
    documented in the release record.
#>
[CmdletBinding()]
param(
    # Repo root; default assumes the script lives in build/.
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$lockPath  = Join-Path $PSScriptRoot 'ffmpeg.lock.json'
$toolsDir  = Join-Path $RepoRoot 'tools\ffmpeg'
$lock      = Get-Content $lockPath -Raw | ConvertFrom-Json

$archivePath = Join-Path $toolsDir $lock.assetName
$binDir      = Join-Path $toolsDir 'bin'
$capsPath    = Join-Path $toolsDir 'capabilities.json'

New-Item -ItemType Directory -Force $toolsDir | Out-Null

# --- 1. Download (skipped when the archive already verifies) ---------------------
function Get-Sha256([string]$path) {
    (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

$needDownload = $true
if (Test-Path $archivePath) {
    if ((Get-Sha256 $archivePath) -eq $lock.sha256.ToLowerInvariant()) {
        Write-Host "Archive already present and checksum-verified: $($lock.assetName)"
        $needDownload = $false
    } else {
        Write-Warning "Existing archive fails checksum; re-downloading."
        Remove-Item $archivePath -Force
    }
}

if ($needDownload) {
    $url = "https://github.com/$($lock.repository)/releases/download/$($lock.releaseTag)/$($lock.assetName)"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing
}

# --- 2. Verify checksum (hard gate) ----------------------------------------------
$actual = Get-Sha256 $archivePath
if ($actual -ne $lock.sha256.ToLowerInvariant()) {
    Remove-Item $archivePath -Force
    throw ("FFmpeg archive checksum MISMATCH.`n  expected: $($lock.sha256)`n  actual:   $actual`n" +
           "The download was deleted. Do not bypass this check — it is the supply-chain " +
           "and licence guarantee of SPEC §2/§11.")
}
Write-Host "Checksum OK: $actual"

# --- 3. Extract ffmpeg.exe / ffprobe.exe + licence texts --------------------------
$extractDir = Join-Path $toolsDir 'extracted'
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
Expand-Archive -Path $archivePath -DestinationPath $extractDir

$inner = Get-ChildItem $extractDir -Directory | Select-Object -First 1
if (-not $inner) { throw "Archive layout unexpected: no top-level directory found." }

New-Item -ItemType Directory -Force $binDir | Out-Null
Copy-Item (Join-Path $inner.FullName 'bin\ffmpeg.exe')  $binDir -Force
Copy-Item (Join-Path $inner.FullName 'bin\ffprobe.exe') $binDir -Force

$licDir = Join-Path $toolsDir 'licenses'
New-Item -ItemType Directory -Force $licDir | Out-Null
Get-ChildItem $inner.FullName -Filter 'LICENSE*' | Copy-Item -Destination $licDir -Force
if (Test-Path (Join-Path $inner.FullName 'doc')) {
    Copy-Item (Join-Path $inner.FullName 'doc') (Join-Path $licDir 'doc') -Recurse -Force
}
Remove-Item $extractDir -Recurse -Force

$ffmpeg = Join-Path $binDir 'ffmpeg.exe'

# --- 4. Capability assertions -----------------------------------------------------
Write-Host "Interrogating $ffmpeg"
$filters  = & $ffmpeg -hide_banner -filters  2>$null | Out-String
$encoders = & $ffmpeg -hide_banner -encoders 2>$null | Out-String
$muxers   = & $ffmpeg -hide_banner -muxers   2>$null | Out-String
$banner   = & $ffmpeg -version 2>$null | Out-String

$missing = @()
foreach ($f in $lock.requiredFilters)  { if ($filters  -notmatch "\b$([regex]::Escape($f))\b") { $missing += "filter:$f" } }
foreach ($e in $lock.requiredEncoders) { if ($encoders -notmatch "\b$([regex]::Escape($e))\b") { $missing += "encoder:$e" } }
foreach ($m in $lock.requiredMuxers)   { if ($muxers   -notmatch "\b$([regex]::Escape($m))\b") { $missing += "muxer:$m" } }
if ($missing.Count -gt 0) {
    throw ("The pinned FFmpeg build is missing required capabilities: $($missing -join ', ').`n" +
           "Pin a different build in build/ffmpeg.lock.json. In particular, a build without " +
           "ddagrab cannot capture the desktop at all (SPEC §1: DXGI Desktop Duplication, never GDI).")
}

# Licence-flavor consistency check. The lock file states which flavor is intended
# (a DECISION recorded in docs/developer-guide/design-decisions.md, currently GPL
# for internal-only deployment); the binary must match it, so a copy-paste of the
# wrong asset line can never silently change the product's licence posture.
#   lgpl → libx264/libx265 must be ABSENT (their presence means a GPL build).
#   gpl  → libx264 must be PRESENT (it is the reason the GPL build was chosen).
$flavor = if ($lock.PSObject.Properties['licenceFlavor']) { $lock.licenceFlavor } else { 'lgpl' }
if ($flavor -eq 'lgpl') {
    foreach ($gpl in @('libx264', 'libx265')) {
        if ($encoders -match "\b$gpl\b") {
            throw "Encoder '$gpl' found — this is a GPL build, but the lock says lgpl. Fix build/ffmpeg.lock.json."
        }
    }
} elseif ($flavor -eq 'gpl') {
    if ($encoders -notmatch '\blibx264\b') {
        throw "The lock says gpl but the build has no libx264 — the wrong asset is pinned. Fix build/ffmpeg.lock.json."
    }
} else {
    throw "Unknown licenceFlavor '$flavor' in build/ffmpeg.lock.json — use 'gpl' or 'lgpl'."
}

$optionalPresent = @{}
foreach ($e in $lock.optionalEncoders) {
    $optionalPresent[$e] = [bool]($encoders -match "\b$([regex]::Escape($e))\b")
}

# --- 5. Record the result for the app build and the release record ----------------
$versionLine = ($banner -split "`r?`n")[0].Trim()
$caps = [ordered]@{
    buildId          = $lock.buildId
    releaseTag       = $lock.releaseTag
    assetName        = $lock.assetName
    sha256           = $lock.sha256
    versionBanner    = $versionLine
    requiredFilters  = $lock.requiredFilters
    requiredEncoders = $lock.requiredEncoders
    optionalEncoders = $optionalPresent
    fetchedAtUtc     = (Get-Date).ToUniversalTime().ToString('o')
}
$caps | ConvertTo-Json -Depth 4 | Set-Content $capsPath -Encoding utf8

Write-Host ""
Write-Host "FFmpeg ready: $versionLine"
Write-Host "  ddagrab present: yes (asserted)"
foreach ($kv in $optionalPresent.GetEnumerator()) {
    Write-Host ("  {0} present: {1}" -f $kv.Key, $(if ($kv.Value) { 'yes' } else { 'NO — software fallback will use native encoders' }))
}
Write-Host "Capabilities written to $capsPath"
