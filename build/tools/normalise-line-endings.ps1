<#
.SYNOPSIS
    Rewrites every text source file in the repository as UTF-8 without a BOM and
    with CRLF line endings.

.DESCRIPTION
    The repository's convention is UTF-8 (no byte-order mark) with Windows line
    endings, and `dotnet format` fails the build when a file drifts from it. Tools
    that edit files in place — sed, python, an editor configured for LF — silently
    break the convention, and the resulting diff is every line of the file.

    Run this after any bulk edit, before building. It only rewrites files that are
    actually wrong, so a clean tree produces no changes and no git churn.
#>
[CmdletBinding()]
param(
    # Repository root. Defaults to the folder two levels above this script.
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

# Extensions that are text we own. Anything not listed (binaries, .ico, .zip) is
# left completely alone.
$extensions = '.cs', '.xaml', '.csproj', '.props', '.json', '.md', '.ps1', '.iss',
              '.yml', '.editorconfig', '.txt', '.sln', '.config'

$changed = 0
Get-ChildItem -Path $Root -Recurse -File |
    Where-Object {
        $extensions -contains $_.Extension -and
        $_.FullName -notmatch '[\/](obj|bin|artifacts|publish|\.git)[\/]'
    } |
    ForEach-Object {
        $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
        if ($bytes.Length -eq 0) { return }

        # Decode as UTF-8, dropping a BOM if one is present.
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $offset = if ($hasBom) { 3 } else { 0 }
        $text = [System.Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)

        # Normalise to LF first so mixed endings collapse, then to CRLF.
        $normalised = $text -replace "`r`n", "`n" -replace "`r", "`n" -replace "`n", "`r`n"

        $wanted = [System.Text.Encoding]::UTF8.GetBytes($normalised)   # UTF8Encoding here emits no BOM
        if ($hasBom -or -not [System.Linq.Enumerable]::SequenceEqual([byte[]]$bytes, [byte[]]$wanted)) {
            [System.IO.File]::WriteAllBytes($_.FullName, $wanted)
            $script:changed++
            Write-Verbose "normalised $($_.FullName)"
        }
    }

Write-Host "Line endings normalised: $changed file(s) rewritten."
