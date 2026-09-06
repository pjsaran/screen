# Environment setup

## What you need

| | Version | Why |
|---|---|---|
| **Windows** | 10 build 17763 or newer, 64-bit | Captr P/Invokes Windows APIs and captures the desktop. It does not build or run anywhere else. |
| **.NET SDK** | 10.0.400 or newer | Pinned in `global.json` with `rollForward: latestFeature`, so a newer 10.0.x patch is fine and 11.0 is not. |
| **PowerShell 7** (`pwsh`) | 7.0+ | Every build script. Windows PowerShell 5.1 is not enough. |
| **Git** | any recent | The build stamps the source commit into the binaries. |

Optional, and only for the jobs that need them:

| | For |
|---|---|
| **GitHub CLI** (`gh`) | Creating the repository and publishing releases. |
| **A code-signing certificate** | Signing release binaries. Everything builds unsigned, loudly. |

You do **not** need to install FFmpeg or Inno Setup. The build fetches both, pinned
to an exact version and verified by checksum, into `tools/`.

## First build

```powershell
git clone <your-remote> screen_recorder
cd screen_recorder
pwsh build/build.ps1
```

The first run takes a few minutes: it restores the .NET tools, downloads the pinned
FFmpeg (about 80 MB) and asserts its capabilities, runs the licence gate, checks
formatting, builds with warnings-as-errors, and runs the unit and CI-safe
integration tests.

`dotnet build` and `dotnet test` also work straight from a clone without running
`build.ps1` first — the integration test project fetches the pinned FFmpeg itself
when `tools/ffmpeg` is missing, since `tools/` is deliberately not in git. Set
`-p:CaptrSkipFfmpegFetch=true` to suppress that.

One trap worth knowing: **do not pass `--nologo` to `dotnet test`.** The test
projects run on Microsoft.Testing.Platform, which does not accept that flag and
reports `Zero tests ran` with a non-zero exit instead of an error — a failure that
looks exactly like a broken repository.

A green run ends with a test summary and no warnings. Anything else is a real
failure — there are no expected warnings in this build.

## To run the application

```powershell
pwsh build/build.ps1 -Publish
./publish/Captr.App.exe
```

`-Publish` produces a self-contained folder with the app, the CLI (as `captr.exe`),
the .NET runtime, and FFmpeg — the same payload the installer wraps. Running from
`publish/` is much closer to a real installation than `dotnet run`, which has no
FFmpeg beside it.

For a quick UI loop, `dotnet build` and then running
`src/Captr.App/bin/Debug/net10.0-windows10.0.19041.0/Captr.App.exe` is fine for
anything that does not actually record.

> Captr is single-instance. Launching a second copy focuses the first one, so kill
> `Captr.App.exe` (both the UI and the `--host` process) before running a rebuild,
> or the build will fail unable to overwrite a locked file.

## Repository layout

```
Captr.slnx                  the solution
Directory.Build.props       shared build settings for every project
Directory.Packages.props    central package versions
Version.props               THE version — never write one anywhere else
BannedSymbols.txt           APIs the analyzer refuses (sync-over-async, plaintext secrets)
SPEC.md                     the original specification, unmodified

src/Captr.Core/             everything that is not WPF; one folder per concern,
                            each with its own README
src/Captr.App/              the WPF window, and the headless host under --host
src/Captr.Cli/              a thin console front-end; every verb's logic is in Core

tests/Captr.Core.Tests/     unit tests. No FFmpeg, no hardware, no network
tests/Captr.Integration.Tests/
                            real FFmpeg, real IPC, real disk; categorised by trait

build/                      every script; see the Building and Releasing pages
docs/                       this documentation
tools/                      fetched, not committed: FFmpeg, Inno Setup
```

`src/Captr.Core` is deliberately folder-per-concern — `Displays`, `Encoders`,
`Supervision`, `Sessions`, `Ipc`, `Transfers`, `Secrets`, `Settings`, `Naming`,
`Cli`, `Diagnostics`, `WindowsEvents`. **Each has a `README.md` explaining what it
owns and where to start reading.** Read the folder's README before its code.

## Editor settings

`.editorconfig` carries the style, and `dotnet format --verify-no-changes` is a
build gate, so a misconfigured editor fails the build rather than producing a
surprising diff. Two things it enforces that tools often get wrong:

- **UTF-8 without a byte-order mark.**
- **CRLF line endings.**

**You do not have to configure git for this.** `.gitattributes` pins the whole tree
to `text=auto eol=crlf`, so every checkout produces CRLF whatever your local
`core.autocrlf` happens to be. That file is load-bearing: before it existed, a clone
on a machine with `core.autocrlf=false` checked out LF and `dotnet format
--verify-no-changes` reported **23,604 ENDOFLINE errors** — on a tree nobody had
touched. The developer who hits that has no way to distinguish it from a real
formatting mistake, so they run `dotnet format` and turn a config gap into a
whole-repository diff.

The eleven golden argument files under `tests/Captr.Core.Tests/Golden` are pinned to
LF instead, because the tests regenerate them with `"\n"` and a golden file should
show a diff only when the arguments actually changed.

### If you cloned before `.gitattributes` landed

An existing working tree keeps whatever line endings it was checked out with. Commit
or stash anything you care about, then let git rewrite it:

```powershell
git rm --cached -r .        # forget the working-tree state, keeps files on disk
git reset --hard            # re-checkout, now applying .gitattributes
```

`git ls-files --eol` should then report `w/crlf` for everything except the golden
files and the icons. Re-cloning does the same job.

Anything that edits files in bulk — `sed`, a script, an editor set to LF — can still
break the encoding or the endings inside a working tree.
`build/tools/normalise-line-endings.ps1` puts it back:

```powershell
pwsh build/tools/normalise-line-endings.ps1
```

It only rewrites files that are actually wrong, so a clean tree produces no diff.

## Regenerating the icons

The application and tray icons are **generated from code** rather than committed as
opaque binaries, so the design is reviewable and adjustable without a graphics tool:

```powershell
pwsh build/make-icons.ps1
```

The resulting `.ico` files are committed. Only re-run this when the design changes.
