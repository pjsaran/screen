<#
.SYNOPSIS
    One-command build for Captr: format check, licence gate, build, test, publish,
    and (optionally) installer — idempotent and loud on failure (SPEC §11).

.DESCRIPTION
    Steps, in order (each can be skipped with a switch where that makes sense):
      1. dotnet tool restore + fetch pinned FFmpeg (checksum-verified)
      2. licence gate (build/check-licenses.ps1) — fails on disallowed or drifted report
      3. dotnet format --verify-no-changes
      4. dotnet build -warnaserror (Release by default)
      5. dotnet test (unit always; integration filtered by -TestFilter)
      6. dotnet publish self-contained win-x64 into publish/ (App + CLI share a folder)
      7. -Installer: build/make-installer.ps1 (arrives with WP11)
    Signing happens inside publish/installer steps via build/sign.ps1 and is skipped
    with a prominent warning when the CAPTR_SIGN_* environment variables are absent.

.PARAMETER Full
    Also run local-machine-only test categories (Display, Gpu) that need a real
    desktop and real hardware encoders.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipFormat,
    [switch]$Full,
    [switch]$Installer,
    [switch]$Publish,
    [string]$TestFilter = 'Category=Ffmpeg',
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $RepoRoot
try {
    $sln = Join-Path $RepoRoot 'Captr.slnx'

    Write-Host '== 1/7 Tools + FFmpeg =========================================' -ForegroundColor Cyan
    # If this checkout arrived as a zip/USB copy rather than a git clone, Windows tags
    # every file "from another computer" (Mark of the Web). The .NET 10 SDK refuses to
    # read a tagged tool manifest, so 'dotnet tool restore' silently installs nothing and
    # the licence gate later crashes with no nuget-license tool. Unblock-File removes the
    # tag and is a harmless no-op on a clean clone.
    Unblock-File (Join-Path $RepoRoot 'dotnet-tools.json')
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed (see output above). The licence gate needs the nuget-license tool from dotnet-tools.json.' }
    & (Join-Path $PSScriptRoot 'fetch-ffmpeg.ps1')

    Write-Host '== 2/7 Licence gate ===========================================' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'check-licenses.ps1')

    if (-not $SkipFormat) {
        Write-Host '== 3/7 Formatting =============================================' -ForegroundColor Cyan
        dotnet format $sln --verify-no-changes
        if ($LASTEXITCODE -ne 0) { throw 'Formatting differences found. Run: dotnet format Captr.slnx' }
    }

    Write-Host '== 4/7 Build (warnings are errors) ============================' -ForegroundColor Cyan
    dotnet build $sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    if (-not $SkipTests) {
        Write-Host '== 5/7 Tests ==================================================' -ForegroundColor Cyan
        dotnet test (Join-Path $RepoRoot 'tests\Captr.Core.Tests') --configuration $Configuration --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }

        # Chaos is CI-safe (all lavfi-based) so it always runs. Display/Gpu need a
        # real desktop and GPU; Soak needs both AND patience — only with -Full.
        # The soak's length comes from CAPTR_SOAK_MINUTES (default 6).
        $filter = if ($Full) {
            "$TestFilter|Category=Chaos|Category=Display|Category=Gpu|Category=Soak"
        } else {
            "$TestFilter|Category=Chaos"
        }
        dotnet test (Join-Path $RepoRoot 'tests\Captr.Integration.Tests') --configuration $Configuration --no-build --filter $filter
        if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    }

    if ($Publish -or $Installer) {
        Write-Host '== 6/7 Publish (self-contained win-x64) =======================' -ForegroundColor Cyan
        $pubDir = Join-Path $RepoRoot 'publish'
        if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force } # idempotent: no stale files
        # App first, then CLI into the SAME folder so the self-contained runtime is
        # shared once (SPEC §4: one install payload).
        dotnet publish (Join-Path $RepoRoot 'src\Captr.App\Captr.App.csproj') -c $Configuration -r win-x64 --self-contained -o $pubDir
        if ($LASTEXITCODE -ne 0) { throw 'Publish (App) failed.' }
        dotnet publish (Join-Path $RepoRoot 'src\Captr.Cli\Captr.Cli.csproj') -c $Configuration -r win-x64 --self-contained -o $pubDir
        if ($LASTEXITCODE -ne 0) { throw 'Publish (CLI) failed.' }

        # The CLI ships as captr.exe but its ASSEMBLY is Captr.Cli (see the csproj
        # comment: captr.dll would overwrite Captr.dll case-insensitively). Renaming
        # only the apphost is safe - it locates Captr.Cli.dll by embedded name.
        Move-Item (Join-Path $pubDir 'Captr.Cli.exe') (Join-Path $pubDir 'captr.exe') -Force

        # Bundle the encoder + its licence texts next to the app (SPEC §2).
        $ffDir = Join-Path $pubDir 'ffmpeg'
        New-Item -ItemType Directory -Force $ffDir | Out-Null
        Copy-Item (Join-Path $RepoRoot 'tools\ffmpeg\bin\*') $ffDir -Force
        Copy-Item (Join-Path $RepoRoot 'tools\ffmpeg\capabilities.json') $ffDir -Force
        Copy-Item (Join-Path $RepoRoot 'tools\ffmpeg\licenses') (Join-Path $ffDir 'licenses') -Recurse -Force

        & (Join-Path $PSScriptRoot 'sign.ps1') -Target $pubDir

        # The published payload exists only now, so the tests that drive it run
        # here rather than in step 5 with everything else.
        if (-not $SkipTests) {
            Write-Host '   Published-payload tests (CLI contract) ---------------------' -ForegroundColor Cyan
            dotnet test (Join-Path $RepoRoot 'tests\Captr.Integration.Tests') --configuration $Configuration --no-build --filter 'Category=Published'
            if ($LASTEXITCODE -ne 0) { throw 'Published-payload tests failed.' }
        }
    }

    if ($Installer) {
        Write-Host '== 7/7 Installer ==============================================' -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'make-installer.ps1')
    }

    Write-Host 'Build pipeline completed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
