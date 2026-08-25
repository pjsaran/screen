<#
.SYNOPSIS
    Produces and publishes a Captr release: build, test, package, sign, verify, tag,
    and upload the installer to a GitHub release.

.DESCRIPTION
    One command, in the only order that is safe:

      1. Refuse to release from a dirty working tree, or from a version that is
         already tagged. A release has to be reproducible from a commit.
      2. Optionally set the version first (-Version / -Bump), then commit that bump.
      3. Full build: licence gate, format check, warnings-as-errors compile, unit and
         integration tests, self-contained publish.
      4. Installer: build/make-installer.ps1 — Inno Setup, signing, SHA256SUMS.txt,
         and release.json.
      5. Verify the artefacts exist and match their recorded hashes.
      6. Tag the commit v<version> and push the branch and the tag.
      7. Create the GitHub release and attach the installer, the checksums, and the
         release record.

    Nothing is pushed and no release is created until every earlier step has passed,
    so a failed build leaves the repository exactly as it was apart from the version
    bump (which is a normal commit you can revert).

.PARAMETER Version
    Set this version before building. Passed straight to set-version.ps1.

.PARAMETER Bump
    Raise major, minor, or patch before building. Passed to set-version.ps1.

.PARAMETER Notes
    Release notes. Defaults to the commit subjects since the previous tag, which is
    a far better starting point than an empty box.

.PARAMETER Draft
    Create the GitHub release as a draft, so it can be edited before anyone sees it.

.PARAMETER SkipPublish
    Build, package, and verify, but do not tag, push, or touch GitHub. Use this to
    rehearse a release.

.PARAMETER Full
    Include the hardware test categories (Display, Gpu, Soak) in step 3. Slower, and
    only meaningful on a machine with a real desktop and GPU.

.EXAMPLE
    pwsh build/new-release.ps1 -Bump minor
    Bumps 0.1.0 to 0.2.0, builds everything, tags v0.2.0, and publishes the release.

.EXAMPLE
    pwsh build/new-release.ps1 -SkipPublish
    A full rehearsal: everything except tagging and publishing.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [ValidateSet('major', 'minor', 'patch')]
    [string] $Bump,
    [string] $Notes,
    [switch] $Draft,
    [switch] $SkipPublish,
    [switch] $Full,
    [string] $RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step([string] $text) {
    Write-Host ''
    Write-Host ("== $text ".PadRight(70, '=')) -ForegroundColor Cyan
}

<#
    Release notes from the commits since the previous tag. A generated starting point
    beats an empty box: it is accurate, it is in the right order, and editing it down
    is quicker than remembering what went in.
#>
function Get-DefaultNotes {
    param([string] $RepoRoot, [string] $Tag)

    $previous = (& git -C $RepoRoot describe --tags --abbrev=0 2>$null)
    $range = if ($previous) { "$previous..HEAD" } else { 'HEAD' }
    $subjects = & git -C $RepoRoot log --no-merges --pretty=format:'- %s' $range

    $header = if ($previous) { "Changes since $previous" } else { 'First release' }
    $body = if ($subjects) { $subjects -join "`n" } else { '- No commits recorded.' }

    return @"
$header

$body

## Install

Download ``captr-setup-*.exe`` below and run it. Verify the download against
``SHA256SUMS.txt`` first if you want to; ``release.json`` records the exact commit,
FFmpeg build, and .NET version this installer was produced from.
"@
}

Push-Location $RepoRoot
try {
    # ---- 1. The tree must be releasable -----------------------------------------
    Step '1/7 Preflight'
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is not on PATH.' }

    $dirty = (& git status --porcelain) | Where-Object { $_ }
    if ($dirty) {
        $dirty | ForEach-Object { Write-Host "  $_" }
        throw 'The working tree has uncommitted changes. Commit or stash them, then release.'
    }

    if (-not $SkipPublish -and -not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'The GitHub CLI (gh) is not installed. Install it, or pass -SkipPublish to build without releasing.'
    }

    # ---- 2. Version --------------------------------------------------------------
    Step '2/7 Version'
    if ($Version -or $Bump) {
        if ($Bump) { & (Join-Path $PSScriptRoot 'set-version.ps1') -Bump $Bump }
        else { & (Join-Path $PSScriptRoot 'set-version.ps1') -Version $Version }
        if ($LASTEXITCODE -ne 0) { throw 'Setting the version failed.' }
    }

    [xml] $props = Get-Content (Join-Path $RepoRoot 'Version.props')
    $releaseVersion = $props.Project.PropertyGroup.CaptrVersion.Trim()
    $tag = "v$releaseVersion"
    Write-Host "Releasing $tag"

    if ((& git tag --list $tag)) {
        throw "$tag already exists. Bump the version first: pwsh build/set-version.ps1 -Bump patch"
    }

    # The version bump is committed BEFORE the build so the artefacts carry the
    # commit they were actually built from (make-installer records it in release.json).
    if ((& git status --porcelain) | Where-Object { $_ }) {
        & git add Version.props
        & git commit -m "Release $tag"
        if ($LASTEXITCODE -ne 0) { throw 'Committing the version bump failed.' }
    }

    # ---- 3. Build and test -------------------------------------------------------
    Step '3/7 Build and test'
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish -Full:$Full
    if ($LASTEXITCODE -ne 0) { throw 'Build or tests failed; nothing has been tagged or published.' }

    # ---- 4. Installer ------------------------------------------------------------
    Step '4/7 Installer'
    & (Join-Path $PSScriptRoot 'make-installer.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

    # ---- 5. Verify what we are about to publish ----------------------------------
    Step '5/7 Verify artefacts'
    $artifacts = Join-Path $RepoRoot 'artifacts'
    $setupExe = Join-Path $artifacts "captr-setup-$releaseVersion.exe"
    $sums = Join-Path $artifacts 'SHA256SUMS.txt'
    $record = Join-Path $artifacts 'release.json'

    foreach ($required in $setupExe, $sums, $record) {
        if (-not (Test-Path $required)) { throw "Expected release artefact is missing: $required" }
    }

    # Re-hash rather than trusting the file that was written a moment ago: this is
    # the last chance to notice a truncated or half-written installer.
    $recorded = (Get-Content $record -Raw | ConvertFrom-Json)
    $actualHash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $recorded.files.((Split-Path $setupExe -Leaf))
    if ($actualHash -ne $expectedHash) {
        throw "The installer's hash does not match release.json ($actualHash vs $expectedHash)."
    }

    $sizeMb = [Math]::Round((Get-Item $setupExe).Length / 1MB, 1)
    Write-Host "  $(Split-Path $setupExe -Leaf)  $sizeMb MB  sha256:$actualHash"
    if (-not $recorded.signed) {
        Write-Warning 'The installer is NOT signed. Set CAPTR_SIGN_PFX and CAPTR_SIGN_PASSWORD to sign it (see docs/developer-guide/releasing.md).'
    }

    if ($SkipPublish) {
        Write-Host "`nRehearsal complete. Nothing was tagged or published." -ForegroundColor Green
        Write-Host "Artefacts are in $artifacts"
        return
    }

    # ---- 6. Tag and push ---------------------------------------------------------
    Step '6/7 Tag and push'
    if (-not $Notes) { $Notes = Get-DefaultNotes -RepoRoot $RepoRoot -Tag $tag }

    & git tag -a $tag -m "Captr $releaseVersion"
    if ($LASTEXITCODE -ne 0) { throw "Creating tag $tag failed." }

    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    & git push origin $branch
    if ($LASTEXITCODE -ne 0) { throw "Pushing $branch failed." }
    & git push origin $tag
    if ($LASTEXITCODE -ne 0) { throw "Pushing tag $tag failed." }

    # ---- 7. GitHub release -------------------------------------------------------
    Step '7/7 GitHub release'
    $notesFile = Join-Path $artifacts 'release-notes.md'
    Set-Content $notesFile $Notes -Encoding utf8

    $ghArgs = @(
        'release', 'create', $tag,
        $setupExe, $sums, $record,
        '--title', "Captr $releaseVersion",
        '--notes-file', $notesFile
    )
    if ($Draft) { $ghArgs += '--draft' }

    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw 'Creating the GitHub release failed. The tag has been pushed; re-run gh release create by hand.' }

    Write-Host "`nReleased $tag" -ForegroundColor Green
    & gh release view $tag --web 2>$null
}
finally {
    Pop-Location
}
