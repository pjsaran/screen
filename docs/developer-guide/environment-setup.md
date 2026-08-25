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

Anything that edits files in bulk — `sed`, a script, an editor set to LF — can
break both silently. `build/tools/normalise-line-endings.ps1` puts the whole tree
back:

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
