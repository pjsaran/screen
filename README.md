# Captr

A lightweight Windows screen recorder built for one job: **capture what is on the
screen, for hours, unattended, and never lose the footage.** One or more displays
are recorded into crash-safe segmented video, driven from a small desktop window or
entirely from the command line (so Windows Task Scheduler can run it).

Built to `SPEC.md`; the design priorities, in order:

1. **The recording survives.** Crash, forced kill, power loss — never an
   unplayable file, never more lost than the segment in progress.
2. **No licensing ambiguity.** Every dependency is unambiguously free for
   commercial use; an automated gate fails the build otherwise.
3. **Light.** Nothing runs when nothing is being recorded. No service, no
   scheduled task, no resident agent, no telemetry.
4. **Scriptable.** Full command-line parity, meaningful exit codes.

## Documentation

Everything is in **[`docs/`](docs/)**, split by who is reading it.

**[User guide](docs/user-guide/)** — installing and using Captr:
[installation](docs/user-guide/installation.md) ·
[getting started](docs/user-guide/getting-started.md) ·
[recording](docs/user-guide/recording.md) ·
[destinations and transfers](docs/user-guide/destinations-and-transfers.md) ·
[naming patterns](docs/user-guide/naming-patterns.md) ·
[SharePoint setup](docs/user-guide/sharepoint-setup.md) ·
[command line](docs/user-guide/command-line.md) ·
[scheduled recording](docs/user-guide/scheduling.md) ·
[troubleshooting](docs/user-guide/troubleshooting.md)

**[Developer guide](docs/developer-guide/)** — building and changing it:
[architecture](docs/developer-guide/architecture.md) ·
[environment setup](docs/developer-guide/environment-setup.md) ·
[building](docs/developer-guide/building.md) ·
[testing](docs/developer-guide/testing.md) ·
[releasing](docs/developer-guide/releasing.md) ·
[upgrading FFmpeg](docs/developer-guide/upgrading-ffmpeg.md) ·
[design decisions](docs/developer-guide/design-decisions.md) ·
[release verification](docs/developer-guide/release-verification.md)

**Legal:** [bundled FFmpeg licence obligations and source
offer](docs/ffmpeg-source-offer.md).

## Building from source

Windows 10/11 x64, the .NET 10 SDK, PowerShell 7, and git. Nothing else — the build
fetches FFmpeg and Inno Setup itself, pinned and checksum-verified.

```powershell
pwsh build/build.ps1                        # licence gate, format, build, tests
pwsh build/build.ps1 -Publish -Installer    # + self-contained publish + installer
```

Full detail in [environment setup](docs/developer-guide/environment-setup.md) and
[building](docs/developer-guide/building.md).

## A tour in five commands

```powershell
captr version                     # exactly which build this is
captr start --label morning       # begins recording every attached display
captr status                      # state, elapsed, coverage, encoder, disk left
captr stop                        # finalises and hands off to the transfer queue
captr recordings list             # what you have
```

## Choosing how it records

Three independent settings, each offered in the window and on `captr start`,
because they answer three different questions:

| Setting | Question | Options |
|---|---|---|
| **Framerate** | How often is the screen sampled? | 5 · 10 · 15 (default) · 20 · 24 · 30 · 45 · 60 FPS |
| **Preset** | How much CPU may the encoder spend per frame? | Ultrafast → Fast; *faster* costs less CPU and makes **bigger** files |
| **Quality** | How good must the picture look? | Lossless · Maximum · High · Balanced (default) · Compact |

Every option is labelled with its trade-off, so nobody needs to know what CRF means
to choose sensibly. All three can be *lowered* while a recording is in progress;
raising any of them waits until it stops.

## Where your data lives

Everything Captr keeps lives under `%LOCALAPPDATA%\Captr` — settings, recordings,
logs, the transfer queue, and the encoder cache — so a backup or a clean-up is one
folder. Credentials are the exception: they live in Windows Credential Manager and
never in a file. The uninstaller keeps all of it unless you explicitly say
otherwise.
