<#
.SYNOPSIS
    Fails the build if any file Captr ships is unsigned, untimestamped, or signed by
    the wrong publisher.

.DESCRIPTION
    Every Windows executable and DLL under -Path (folders, searched recursively, and
    single files such as the installer) must have:

      * a signature Windows accepts (Status Valid - or, with -AllowUntrustedRoot for a
        self-signed rehearsal, a chain whose ONLY fault is the untrusted root);
      * an RFC 3161 timestamp, so the signature outlives the certificate;
      * the right signer: Captr's own files (build/signing.lock.json, ownFilePatterns -
        our binaries, the bundled FFmpeg, the installer and uninstaller) must be signed
        by -ExpectedPublisher and nobody else; any other file by -ExpectedPublisher or
        one of the trusted third-party publishers listed there.

    Prints one line per problem and exits non-zero if there are any. -ReportPath
    writes the result as JSON, which make-installer.ps1 records in release.json - so
    "signed": true there means verified, not merely "a certificate was configured".

.PARAMETER ExpectedPublisher
    The common name on Captr's signing certificate. Defaults to CAPTR_SIGN_PUBLISHER.

.EXAMPLE
    pwsh build/Verify-Signatures.ps1 -Path publish, artifacts\captr-setup-0.2.0.exe -ExpectedPublisher 'Contoso Ltd'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [string] $ExpectedPublisher = $env:CAPTR_SIGN_PUBLISHER,
    [switch] $AllowUntrustedRoot,
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib\Signing.ps1')

if (-not $ExpectedPublisher) {
    throw ('Verify-Signatures needs the publisher to check for: pass -ExpectedPublisher, or set CAPTR_SIGN_PUBLISHER ' +
           "to the common name on Captr's signing certificate.")
}

$trusted = @((Get-SigningLock).trustedThirdPartyPublishers.names)

$files = foreach ($item in $Path) {
    if (Test-Path $item -PathType Container) {
        Get-ChildItem $item -Recurse -File | Where-Object { Test-PortableExecutable $_.FullName } | ForEach-Object FullName
    } elseif (Test-Path $item -PathType Leaf) {
        (Resolve-Path $item).Path
    } else {
        throw "Nothing to verify at $item."
    }
}

$problems = [System.Collections.Generic.List[string]]::new()
$checked = 0
foreach ($file in $files) {
    $checked++
    $facts = Get-SignatureFacts $file

    if (-not $facts.Signer) {
        $problems.Add("UNSIGNED        $file")
        continue
    }

    $acceptable = $facts.Status -eq 'Valid' -or ($AllowUntrustedRoot -and $facts.UntrustedRootOnly)
    if (-not $acceptable) {
        $problems.Add("INVALID         $file  ($($facts.Status): $($facts.StatusMessage))")
    }

    if (-not $facts.Timestamped) {
        $problems.Add("NOT TIMESTAMPED $file")
    } elseif (-not $facts.Rfc3161) {
        $problems.Add("NOT RFC 3161    $file  (legacy Authenticode timestamp)")
    }

    $allowed = if (Test-OwnFile $file) { @($ExpectedPublisher) } else { @($ExpectedPublisher) + $trusted }
    if ($facts.Signer -notin $allowed) {
        $problems.Add("WRONG SIGNER    $file  (signed by '$($facts.Signer)'; expected $(($allowed | ForEach-Object { "'$_'" }) -join ' or '))")
    }
}

if ($ReportPath) {
    [ordered]@{
        verifiedAtUtc     = (Get-Date).ToUniversalTime().ToString('o')
        expectedPublisher = $ExpectedPublisher
        filesChecked      = $checked
        problems          = @($problems)
        passed            = $problems.Count -eq 0
    } | ConvertTo-Json -Depth 3 | Set-Content $ReportPath -Encoding utf8
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw "$($problems.Count) of $checked shipped file(s) failed signature verification."
}

Write-Host "All $checked shipped file(s) are signed by an expected publisher with an RFC 3161 timestamp." -ForegroundColor Green
