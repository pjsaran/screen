<#
.SYNOPSIS
    Creates the GitHub repository for Captr and pushes this working copy to it.

.DESCRIPTION
    A one-time bootstrap for a fresh clone-less repository. It:

      1. Checks the GitHub CLI is installed and signed in.
      2. Makes sure this folder is a git repository with at least one commit.
      3. Creates the repository on GitHub (private by default — a screen recorder's
         source is yours to decide about, and making a repository public later is one
         click while un-publishing is not).
      4. Adds it as 'origin' and pushes the current branch, setting upstream.

    Safe to run more than once: an existing repository or an existing 'origin' is
    reported and left alone rather than overwritten.

.PARAMETER Name
    Repository name. Defaults to the folder name.

.PARAMETER Owner
    GitHub user or organisation. Defaults to the signed-in account.

.PARAMETER Public
    Create a public repository instead of a private one.

.PARAMETER Description
    The repository's one-line description.

.EXAMPLE
    pwsh build/create-github-repo.ps1 -Name captr -Public
#>
[CmdletBinding()]
param(
    [string] $Name,
    [string] $Owner,
    [switch] $Public,
    [string] $Description = 'Captr — a lightweight, crash-survivable Windows screen recorder.',
    [string] $RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $RepoRoot
try {
    # ---- 1. Tooling ---------------------------------------------------------------
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'The GitHub CLI (gh) is not installed. Get it from https://cli.github.com, then run: gh auth login'
    }

    & gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Not signed in to GitHub. Run: gh auth login' }

    if (-not $Name) { $Name = Split-Path $RepoRoot -Leaf }

    # ---- 2. A local repository with something in it --------------------------------
    if (-not (Test-Path (Join-Path $RepoRoot '.git'))) {
        Write-Host 'Initialising a git repository here'
        & git init -b main
        if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }
    }

    if (-not (& git -C $RepoRoot rev-parse --verify HEAD 2>$null)) {
        throw 'This repository has no commits yet. Commit your work first, then run this script.'
    }

    # ---- 3. Create it on GitHub ----------------------------------------------------
    $slug = if ($Owner) { "$Owner/$Name" } else { $Name }

    if (& gh repo view $slug 2>$null) {
        Write-Host "$slug already exists on GitHub; leaving it as it is."
    }
    else {
        $visibility = if ($Public) { '--public' } else { '--private' }
        Write-Host "Creating $slug ($(if ($Public) { 'public' } else { 'private' }))"
        & gh repo create $slug $visibility --description $Description --disable-wiki
        if ($LASTEXITCODE -ne 0) { throw "Creating $slug failed." }
    }

    # ---- 4. Wire up the remote and push --------------------------------------------
    $remoteUrl = (& gh repo view $slug --json url --jq .url).Trim() + '.git'

    $existingOrigin = (& git -C $RepoRoot remote get-url origin 2>$null)
    if ($existingOrigin) {
        Write-Host "origin is already $existingOrigin; leaving it as it is."
    }
    else {
        & git -C $RepoRoot remote add origin $remoteUrl
        if ($LASTEXITCODE -ne 0) { throw "Adding origin $remoteUrl failed." }
    }

    $branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
    Write-Host "Pushing $branch to origin"
    & git -C $RepoRoot push -u origin $branch
    if ($LASTEXITCODE -ne 0) { throw "Pushing $branch failed." }

    Write-Host ''
    Write-Host "Repository ready: $remoteUrl" -ForegroundColor Green
    Write-Host 'Next: pwsh build/new-release.ps1 -Bump patch   (to cut the first release)'
}
finally {
    Pop-Location
}
