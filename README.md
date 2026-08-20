# Captr

A lightweight Windows screen recorder built for one job: **capture what is on the
screen, for hours, unattended, and never lose the footage.** One or more displays
are recorded into crash-safe segmented video, driven from a small desktop UI or
entirely from the command line (so Windows Task Scheduler can run it).

Built to `SPEC.md`; the design priorities, in order:

1. **The recording survives.** Crash, forced kill, power loss — never an
   unplayable file, never more lost than the segment in progress.
2. **No licensing ambiguity.** Every dependency is unambiguously free for
   commercial use; an automated gate fails the build otherwise.
3. **Light.** Nothing runs when nothing is being recorded.
4. **Scriptable.** Full CLI parity, meaningful exit codes.

## Building from source

Prerequisites: Windows 10/11 x64, .NET 10 SDK, git. Then:

```powershell
pwsh build/fetch-ffmpeg.ps1     # once: pinned LGPL FFmpeg, checksum-verified
pwsh build/build.ps1            # format check, licence gate, build, tests
pwsh build/build.ps1 -Publish -Installer   # + self-contained publish + installer
```

Everything a build produces lands in `publish/` and `artifacts/` (installer,
`SHA256SUMS.txt`, `release.json`). Code signing is driven by environment
variables (`CAPTR_SIGN_PFX`, `CAPTR_SIGN_PFX_PASSWORD`) and the build succeeds
unsigned with a loud warning when they are absent.

Tests: `dotnet test --project tests/Captr.Core.Tests` runs everywhere;
`tests/Captr.Integration.Tests` needs the fetched FFmpeg, and its `Display`/`Gpu`
categories need a real desktop and GPU (`pwsh build/build.ps1 -Full`).

## Documentation

| Topic | Where |
|---|---|
| How the whole thing fits together | `ARCHITECTURE.md` (then per-folder READMEs) |
| What each spec test requirement is covered by | `docs/test-coverage.md` |
| Install / silent install / upgrade / uninstall | `docs/install.md` |
| Scheduled recording (read the session-0 warning!) | `docs/task-scheduler.md` |
| Command-line reference and exit codes | `docs/cli.md` |
| SharePoint destination setup and credential rotation | `docs/graph-setup.md` |
| Bundled FFmpeg licence obligations and source offer | `docs/ffmpeg-source-offer.md` |
| Release checklist incl. deferred clean-machine checks | `docs/release-verification.md` |

## Where your data lives

Settings `%APPDATA%\Captr`; recordings, logs, delivery queue
`%LOCALAPPDATA%\Captr`; credentials in Windows Credential Manager (never in
files). The uninstaller keeps all of it unless you explicitly say otherwise.
