<#
.SYNOPSIS
    Former signing entry point, kept so scripts and habits that call it keep working.

.DESCRIPTION
    Signing now lives in build/Sign-Artifacts.ps1 (three methods, RFC 3161 with
    fallback servers, DLLs and the uninstaller included, FFmpeg verified before it is
    signed, a -DryRun that needs no certificate) and is checked by
    build/Verify-Signatures.ps1. This forwards to the former with the same meaning the
    old -Target had: a folder or a single file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Target
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Sign-Artifacts.ps1') -Path $Target
