<#
.SYNOPSIS
    Authenticode-signs every executable and DLL Captr ships, with SHA-256 and an
    RFC 3161 timestamp, by any of three methods - or, with -DryRun, shows exactly what
    it would do without needing a certificate at all.

.DESCRIPTION
    What gets signed, per file found under -Path (a folder, searched recursively, or
    single files):

      * Captr's own binaries (Captr.*.dll/exe, captr.exe), the installer and uninstaller:
        always, with Captr's certificate.
      * The bundled ffmpeg.exe and ffprobe.exe: only AFTER their pinned digest is
        verified against the capabilities.json beside them (recorded by
        fetch-ffmpeg.ps1 straight after the pinned download's SHA-256 was checked).
        A mismatch stops the run: signing a binary we cannot prove is the pinned
        build would put Captr's name on something unknown. The digest ignores the
        signature, so the application's own runtime check still matches afterwards.
      * Third-party files: left alone when a trusted publisher (build/signing.lock.json)
        has already signed and timestamped them - the .NET runtime, WPF - and signed
        by Captr otherwise, so that nothing we ship is unsigned (Smart App Control and
        WDAC block unsigned DLLs; this was observed on the development machine).

    Methods (credentials come ONLY from the environment or the certificate store - see
    build/lib/Signing.ps1 for every variable, and docs/developer-guide/releasing.md):

      Pfx              The certificate is loaded in this process (never written to
                       disk, its password never put on a command line) and signs
                       with Set-AuthenticodeSignature; the RFC 3161 timestamp is
                       then added by signtool, which needs no key to do it.
      CertStore        signtool, selecting the certificate by thumbprint - works with
                       hardware tokens and EV certificates.
      ArtifactSigning  signtool with the Azure Artifact Signing (Trusted Signing)
                       client library; nothing secret is stored locally.

    Timestamping tries each server in the pinned list (a CAPTR_SIGN_TIMESTAMP_URL, if
    set, goes first), twice each, before failing the run - an untimestamped signature
    dies with its certificate, which is exactly what the timestamp exists to prevent.

    With nothing configured the script prints a prominent warning and the plan, and
    succeeds - a contributor without the certificate must always be able to build.
    -Require turns that into a failure (the release pipeline uses it).

.PARAMETER Path
    Folders (searched recursively for .exe and .dll) and/or single files.

.PARAMETER Force
    Sign every file named explicitly in -Path whatever its current signature and
    extension. Used by the installer build, where Inno Setup hands over temporary
    copies of setup and the uninstaller to be signed.

.EXAMPLE
    pwsh build/Sign-Artifacts.ps1 -Path publish -DryRun
    The plan, with no certificate and no network.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [ValidateSet('Auto', 'Pfx', 'CertStore', 'ArtifactSigning')] [string] $Method = 'Auto',
    [switch] $DryRun,
    [switch] $Require,
    [switch] $Force,
    [string] $Description = 'Captr',
    [string] $DescriptionUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib\Signing.ps1')
. (Join-Path $PSScriptRoot 'lib\PeImageDigest.ps1')

$lock = Get-SigningLock
$trusted = @($lock.trustedThirdPartyPublishers.names)

# ---- 1. What is there ----------------------------------------------------------------
$explicit = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$files = [System.Collections.Generic.List[string]]::new()
foreach ($item in $Path) {
    if (Test-Path $item -PathType Container) {
        Get-ChildItem $item -Recurse -File -Include *.exe, *.dll |
            Where-Object { Test-MzHeader $_.FullName } |
            ForEach-Object { $files.Add($_.FullName) }
    } elseif (Test-Path $item -PathType Leaf) {
        $full = (Resolve-Path $item).Path
        $files.Add($full)
        [void]$explicit.Add($full)
    } else {
        throw "Nothing to sign at $item."
    }
}

<#
    Every FFmpeg binary is checked against its pinned digest before anything else
    happens - in a dry run too, since it needs neither a certificate nor the network.
#>
function Assert-PinnedFfmpeg([string] $File) {
    $folder = Split-Path $File -Parent
    $capabilities = @((Join-Path $folder 'capabilities.json'), (Join-Path $folder '..\capabilities.json')) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $capabilities) {
        throw "Refusing to sign $File`: there is no capabilities.json recording its pinned digest. Run build/fetch-ffmpeg.ps1."
    }

    $record = Get-Content $capabilities -Raw | ConvertFrom-Json
    $name = Split-Path $File -Leaf
    $expected = if ($record.PSObject.Properties['binaryDigests'] -and $record.binaryDigests.PSObject.Properties[$name]) {
        $record.binaryDigests.$name
    } else { $null }
    if (-not $expected) {
        throw "Refusing to sign $File`: $capabilities records no digest for it. Re-run build/fetch-ffmpeg.ps1."
    }

    $actual = Get-PeImageDigest $File
    if ($actual -ne $expected) {
        throw ("Refusing to sign $File`: it is not the pinned FFmpeg build (digest $actual, pinned $expected). " +
               'Re-run build/fetch-ffmpeg.ps1 and publish again.')
    }
}

$plan = foreach ($file in $files) {
    $name = Split-Path $file -Leaf
    if ($name -in 'ffmpeg.exe', 'ffprobe.exe') { Assert-PinnedFfmpeg $file }

    $facts = Get-SignatureFacts $file
    $action = if ($explicit.Contains($file) -and $Force) {
        'sign'
    } elseif (Test-OwnFile $file) {
        'sign'
    } elseif ($facts.Status -eq 'Valid' -and $facts.Timestamped -and $facts.Signer -in $trusted) {
        'keep'
    } else {
        'sign'
    }

    [pscustomobject]@{ File = $file; Action = $action; CurrentSigner = $facts.Signer }
}

$toSign = @($plan | Where-Object Action -eq 'sign')
$toKeep = @($plan | Where-Object Action -eq 'keep')

# ---- 2. How ----------------------------------------------------------------------------
$config = Get-SigningConfig -Method $Method

function Write-Plan {
    Write-Host "Signing plan: $($toSign.Count) file(s) to sign, $($toKeep.Count) already signed by a trusted publisher."
    foreach ($entry in $toSign) { Write-Host "  sign  $($entry.File)" }
    if ($VerbosePreference -eq 'Continue') {
        foreach ($entry in $toKeep) { Write-Host "  keep  $($entry.File)  ($($entry.CurrentSigner))" }
    }
}

if ($DryRun) {
    Write-Plan
    $how = if ($config) { "method $($config.Method); timestamps from $($config.TimestampServers -join ', ')" } else { 'no signing method configured' }
    Write-Host "Dry run ($how). FFmpeg digests verified. Nothing was changed."
    return
}

if (-not $config) {
    if ($Require) {
        throw ('Signing is required but no signing method is configured. Set the environment variables for one of ' +
               'Pfx, CertStore, or ArtifactSigning (docs/developer-guide/releasing.md).')
    }

    Write-Warning '=============================================================='
    Write-Warning ' UNSIGNED BUILD: no signing method is configured.'
    Write-Warning ' The binaries will NOT be Authenticode-signed. Fine for local'
    Write-Warning ' development; a release MUST be signed (SPEC 11).'
    Write-Warning '=============================================================='
    Write-Plan
    return
}

if ($toSign.Count -eq 0) {
    Write-Host 'Nothing needs signing.'
    return
}

# ---- 3. Sign ----------------------------------------------------------------------------
$signtool = Get-SignTool
$descriptionArgs = @('/d', $Description)
if ($DescriptionUrl) { $descriptionArgs += @('/du', $DescriptionUrl) }

function Invoke-SignTool([string[]] $Arguments) {
    $output = & $signtool @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "signtool $($Arguments[0]) failed (exit $LASTEXITCODE):`n$($output | Out-String)"
    }
}

switch ($config.Method) {
    'Pfx' {
        $password = if ($env:CAPTR_SIGN_PFX_PASSWORD) { $env:CAPTR_SIGN_PFX_PASSWORD } else { $env:CAPTR_SIGN_PASSWORD }
        $pfxBytes = if ($env:CAPTR_SIGN_PFX_BASE64) { [Convert]::FromBase64String($env:CAPTR_SIGN_PFX_BASE64) }
                    else { [System.IO.File]::ReadAllBytes($env:CAPTR_SIGN_PFX) }
        $certificate = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12(
            $pfxBytes, $password, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            if (-not $certificate.HasPrivateKey) { throw 'The PFX holds no private key.' }
            foreach ($entry in $toSign) {
                $result = Set-AuthenticodeSignature -FilePath $entry.File -Certificate $certificate -HashAlgorithm SHA256
                if (-not $result.SignerCertificate) {
                    throw "Signing $($entry.File) failed: $($result.StatusMessage)"
                }
            }
        } finally {
            $certificate.Dispose()
            [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
        }
    }
    'CertStore' {
        $storeArgs = @('/sha1', $env:CAPTR_SIGN_CERT_THUMBPRINT, '/s', 'My')
        if ($env:CAPTR_SIGN_CERT_STORE -eq 'LocalMachine') { $storeArgs += '/sm' }
        foreach ($entry in $toSign) {
            Invoke-SignTool (@('sign', '/fd', 'SHA256') + $storeArgs + $descriptionArgs + @($entry.File))
        }
    }
    'ArtifactSigning' {
        $dlib = Get-ArtifactSigningDlib
        $metadata = Join-Path ([System.IO.Path]::GetTempPath()) "captr-artifact-signing-$([Guid]::NewGuid().ToString('N')).json"
        [ordered]@{
            Endpoint               = $env:CAPTR_ARTIFACT_SIGNING_ENDPOINT
            CodeSigningAccountName = $env:CAPTR_ARTIFACT_SIGNING_ACCOUNT
            CertificateProfileName = $env:CAPTR_ARTIFACT_SIGNING_PROFILE
        } | ConvertTo-Json | Set-Content $metadata -Encoding utf8
        try {
            foreach ($entry in $toSign) {
                Invoke-SignTool (@('sign', '/fd', 'SHA256', '/dlib', $dlib, '/dmdf', $metadata) + $descriptionArgs + @($entry.File))
            }
        } finally {
            Remove-Item $metadata -Force -ErrorAction SilentlyContinue
        }
    }
}

# ---- 4. Timestamp (RFC 3161), with fallback servers -------------------------------------
foreach ($entry in $toSign) {
    $stamped = $false
    $errors = @()
    foreach ($server in $config.TimestampServers) {
        foreach ($attempt in 1, 2) {
            $output = & $signtool timestamp /tr $server /td SHA256 $entry.File 2>&1
            if ($LASTEXITCODE -eq 0) { $stamped = $true; break }
            $errors += "$server (attempt $attempt): $(($output | Out-String).Trim())"
            Start-Sleep -Seconds (2 * $attempt)
        }
        if ($stamped) { break }
        Write-Warning "Timestamp server $server failed; trying the next one."
    }

    if (-not $stamped) {
        throw "No timestamp server could timestamp $($entry.File):`n$($errors -join "`n")"
    }
}

# ---- 5. Prove it -------------------------------------------------------------------------
foreach ($entry in $toSign) {
    $facts = Get-SignatureFacts $entry.File
    if (-not $facts.Signer -or -not $facts.Rfc3161) {
        throw "After signing, $($entry.File) is not signed with an RFC 3161 timestamp ($($facts.Status): $($facts.StatusMessage))."
    }
}

Write-Host "Signed and timestamped $($toSign.Count) file(s) with method $($config.Method)."
