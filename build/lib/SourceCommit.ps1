<#
    The source commit a build is stamped with (SPEC §11), and - when git cannot give
    it - exactly why not and what to do. Dot-sourced by build.ps1; kept apart so the
    diagnosis can be tested without running a build.
#>

function Get-SourceCommit {
    param([Parameter(Mandatory)] [string] $RepoRoot)

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'git is not on the PATH, so the build cannot stamp its source commit. Install Git for Windows (https://git-scm.com) and reopen the terminal.'
    }

    $root = (Resolve-Path $RepoRoot).Path.TrimEnd('\')
    $output = & git -C $root rev-parse --short=12 HEAD 2>&1
    if ($LASTEXITCODE -eq 0) {
        return "$output".Trim()
    }

    $message = "$($output | Out-String)".Trim()
    $clone = 'git clone https://github.com/pjsaran/screen.git <new folder>'

    if ($message -match 'dubious ownership') {
        throw ("git refuses to read $root because the folder is owned by another account:`n$message`n" +
               "Run:  git config --global --add safe.directory `"$($root -replace '\\', '/')`"")
    }

    if ($message -match 'not a git repository') {
        throw ("$root is not a git clone (it was copied or unzipped), so the build cannot stamp its source commit.`n" +
               "Clone it instead:  $clone")
    }

    # git found A repository, but HEAD names no commit. Two ways to get here, and
    # "safe.directory" fixes neither: the folder sits inside some OTHER repository
    # (say C:\Projects was 'git init'-ed), or a copy was 'git init'-ed and never
    # committed. Both used to end in git's bare "Needed a single revision".
    $top = "$(& git -C $root rev-parse --show-toplevel 2>$null)".Trim() -replace '/', '\'
    if ($top -and -not [string]::Equals($top.TrimEnd('\'), $root, [StringComparison]::OrdinalIgnoreCase)) {
        throw ("$root is not a clone of Captr: git is reading the repository at $top instead, which contains it.`n" +
               "That folder is a different repository (look for $top\.git). Clone Captr somewhere outside it:  $clone")
    }

    throw ("The git repository at $root has no commits, so there is no source commit to stamp the build with.`n" +
           "This happens when a copied or unzipped folder is turned into a repository with 'git init'.`n" +
           "Clone Captr instead:  $clone`n(git said: $message)")
}
