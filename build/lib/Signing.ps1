<#
.SYNOPSIS
    Shared helpers for build/Sign-Artifacts.ps1 and build/Verify-Signatures.ps1.

.DESCRIPTION
    Dot-source this file. It owns four things both scripts need to agree on:
      * where the pinned signing tools come from (build/signing.lock.json), and fetching
        and verifying them into tools/signing - no Windows SDK install is needed;
      * how the signing method and its credentials are read - ONLY from environment
        variables (which is what CI secrets become) or the certificate store, never
        from a file in the repository;
      * what the facts of a file's signature are, including whether its timestamp is
        an RFC 3161 one (PowerShell's own signing produces the older kind);
      * which shipped files must carry Captr's signature rather than someone else's.
#>

Set-StrictMode -Version Latest

$script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

function Get-SigningLock {
    Get-Content (Join-Path $script:RepoRoot 'build\signing.lock.json') -Raw | ConvertFrom-Json
}

<#
    Fetches a pinned NuGet package into tools/signing/<id>/<version>, verifying it
    against the SHA-512 nuget.org publishes for it. Returns the extraction folder.
    Idempotent: an existing extraction with its marker file is reused.
#>
function Get-PinnedNuGetPackage {
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $Sha512
    )

    $folder = Join-Path $script:RepoRoot "tools\signing\$($Id.ToLowerInvariant())\$Version"
    $marker = Join-Path $folder '.verified'
    if ((Test-Path $marker) -and (Get-Content $marker -Raw).Trim() -eq $Sha512) {
        return $folder
    }

    New-Item -ItemType Directory -Force $folder | Out-Null
    $package = Join-Path $folder 'package.nupkg'
    $url = "https://api.nuget.org/v3-flatcontainer/$($Id.ToLowerInvariant())/$Version/$($Id.ToLowerInvariant()).$Version.nupkg"
    Write-Host "Fetching $Id $Version (pinned signing tool)"
    Invoke-WebRequest -Uri $url -OutFile $package -UseBasicParsing

    $bytes = [System.IO.File]::ReadAllBytes($package)
    $actual = [Convert]::ToBase64String([System.Security.Cryptography.SHA512]::HashData($bytes))
    if ($actual -ne $Sha512) {
        Remove-Item $package -Force
        throw ("$Id $Version failed its checksum (expected $Sha512, got $actual). The download was deleted. " +
               'Do not bypass this: build/signing.lock.json pins the hash nuget.org publishes for the package.')
    }

    Expand-Archive -Path $package -DestinationPath $folder -Force
    Remove-Item $package -Force
    Set-Content -Path $marker -Value $Sha512 -NoNewline
    return $folder
}

function Get-SignTool {
    $lock = Get-SigningLock
    $folder = Get-PinnedNuGetPackage -Id $lock.signtool.package -Version $lock.signtool.version -Sha512 $lock.signtool.sha512
    $path = Join-Path $folder $lock.signtool.path
    if (-not (Test-Path $path)) { throw "The pinned signtool package has no $($lock.signtool.path)." }
    return $path
}

function Get-ArtifactSigningDlib {
    $lock = Get-SigningLock
    $entry = $lock.artifactSigningClient
    $folder = Get-PinnedNuGetPackage -Id $entry.package -Version $entry.version -Sha512 $entry.sha512
    $path = Join-Path $folder $entry.dlibPath
    if (-not (Test-Path $path)) { throw "The pinned Artifact Signing client has no $($entry.dlibPath)." }
    return $path
}

<#
    The signing configuration, from the environment only. Returns $null when nothing
    is configured - an unsigned (development) build.

      Pfx              CAPTR_SIGN_PFX (a path) or CAPTR_SIGN_PFX_BASE64 (the file's
                       content, as a CI secret), plus CAPTR_SIGN_PFX_PASSWORD
                       (CAPTR_SIGN_PASSWORD is accepted as an older name).
      CertStore        CAPTR_SIGN_CERT_THUMBPRINT, and optionally
                       CAPTR_SIGN_CERT_STORE = CurrentUser (default) | LocalMachine.
      ArtifactSigning  CAPTR_ARTIFACT_SIGNING_ENDPOINT, CAPTR_ARTIFACT_SIGNING_ACCOUNT,
                       CAPTR_ARTIFACT_SIGNING_PROFILE; Azure sign-in through the usual
                       AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET (or a CI
                       workload identity).

    CAPTR_SIGN_METHOD picks one explicitly; otherwise the first configured wins, in
    the order above. CAPTR_SIGN_TIMESTAMP_URL puts a timestamp server first in line.
#>
function Get-SigningConfig {
    param([string] $Method = 'Auto')

    if ($Method -eq 'Auto' -and $env:CAPTR_SIGN_METHOD) { $Method = $env:CAPTR_SIGN_METHOD }
    if ($Method -eq 'Auto') {
        $Method = if ($env:CAPTR_SIGN_PFX -or $env:CAPTR_SIGN_PFX_BASE64) { 'Pfx' }
                  elseif ($env:CAPTR_SIGN_CERT_THUMBPRINT) { 'CertStore' }
                  elseif ($env:CAPTR_ARTIFACT_SIGNING_ENDPOINT) { 'ArtifactSigning' }
                  else { $null }
    }

    if (-not $Method) { return $null }

    $missing = @()
    switch ($Method) {
        'Pfx' {
            if (-not ($env:CAPTR_SIGN_PFX -or $env:CAPTR_SIGN_PFX_BASE64)) { $missing += 'CAPTR_SIGN_PFX or CAPTR_SIGN_PFX_BASE64' }
            if ($env:CAPTR_SIGN_PFX -and -not (Test-Path $env:CAPTR_SIGN_PFX)) { throw "CAPTR_SIGN_PFX points to a missing file: $env:CAPTR_SIGN_PFX" }
            if (-not ($env:CAPTR_SIGN_PFX_PASSWORD -or $env:CAPTR_SIGN_PASSWORD)) { $missing += 'CAPTR_SIGN_PFX_PASSWORD' }
        }
        'CertStore' {
            if (-not $env:CAPTR_SIGN_CERT_THUMBPRINT) { $missing += 'CAPTR_SIGN_CERT_THUMBPRINT' }
        }
        'ArtifactSigning' {
            foreach ($name in 'CAPTR_ARTIFACT_SIGNING_ENDPOINT', 'CAPTR_ARTIFACT_SIGNING_ACCOUNT', 'CAPTR_ARTIFACT_SIGNING_PROFILE') {
                if (-not (Get-Item "env:$name" -ErrorAction SilentlyContinue)) { $missing += $name }
            }
        }
        default { throw "Unknown signing method '$Method'. Use Pfx, CertStore, or ArtifactSigning." }
    }

    if ($missing) {
        throw "Signing method '$Method' is selected but these environment variables are not set: $($missing -join ', ')."
    }

    $lock = Get-SigningLock
    $servers = @(if ($Method -eq 'ArtifactSigning') { $lock.timestampServers.artifactSigning } else { $lock.timestampServers.default })
    if ($env:CAPTR_SIGN_TIMESTAMP_URL) { $servers = @($env:CAPTR_SIGN_TIMESTAMP_URL) + @($servers | Where-Object { $_ -ne $env:CAPTR_SIGN_TIMESTAMP_URL }) }

    [pscustomobject]@{
        Method           = $Method
        TimestampServers = $servers
    }
}

function Test-PortableExecutable([string] $Path) {
    $extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    return $extension -in '.exe', '.dll', '.sys', '.ocx', '.e32', '.tmp' -and (Test-MzHeader $Path)
}

function Test-MzHeader([string] $Path) {
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { return $stream.ReadByte() -eq 0x4D -and $stream.ReadByte() -eq 0x5A } finally { $stream.Dispose() }
    } catch { return $false }
}

<#
    What a file's Authenticode signature says. RFC 3161 is read from the signature
    itself: a counter-signature attribute of 1.3.6.1.4.1.311.3.3.1 is an RFC 3161
    timestamp; 1.2.840.113549.1.9.6 is the older Authenticode kind, which is what
    PowerShell's Set-AuthenticodeSignature produces and which the release rules reject.
#>
function Get-SignatureFacts([string] $Path) {
    $signature = Get-AuthenticodeSignature -FilePath $Path
    $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.GetNameInfo('SimpleName', $false) } else { $null }

    $rfc3161 = $false
    if ($signature.SignerCertificate) {
        try {
            $bytes = [System.IO.File]::ReadAllBytes($Path)
            $pe = [BitConverter]::ToInt32($bytes, 0x3C)
            $optional = $pe + 24
            $security = $optional + $(if ([BitConverter]::ToUInt16($bytes, $optional) -eq 0x20B) { 112 } else { 96 }) + 32
            $offset = [BitConverter]::ToInt32($bytes, $security)
            $length = [BitConverter]::ToInt32($bytes, $security + 4)
            if ($offset -gt 0 -and $length -gt 8) {
                $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
                $cms.Decode($bytes[($offset + 8)..($offset + $length - 1)])
                $rfc3161 = [bool]($cms.SignerInfos[0].UnsignedAttributes | Where-Object { $_.Oid.Value -eq '1.3.6.1.4.1.311.3.3.1' })
            }
        } catch {
            $rfc3161 = $false
        }
    }

    # "Valid" needs a chain to a trusted root. A self-signed rehearsal certificate
    # produces UnknownError with exactly one complaint: the untrusted root.
    $untrustedRootOnly = $signature.Status -eq 'UnknownError' -and
        $signature.StatusMessage -match 'root certificate which is not trusted'

    [pscustomobject]@{
        Path              = $Path
        Status            = [string]$signature.Status
        StatusMessage     = $signature.StatusMessage
        Signer            = $signer
        Timestamped       = [bool]$signature.TimeStamperCertificate
        Rfc3161           = $rfc3161
        UntrustedRootOnly = $untrustedRootOnly
    }
}

function Test-OwnFile([string] $Path) {
    $name = [System.IO.Path]::GetFileName($Path)
    foreach ($pattern in (Get-SigningLock).ownFilePatterns.patterns) {
        if ($name -like $pattern) { return $true }
    }
    return $false
}
