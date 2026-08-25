# Captr documentation

Two audiences, two folders. Nothing is in both.

## [User guide](user-guide/) — using Captr

For anyone installing or running Captr, whether from the window or from a script.

| | |
|---|---|
| [Installation](user-guide/installation.md) | Installing, silent/unattended install, upgrading, uninstalling |
| [Getting started](user-guide/getting-started.md) | Your first recording, the window, the tray, hotkeys |
| [Recording](user-guide/recording.md) | Frame rate, preset, quality, displays, pausing, what happens when things go wrong |
| [Destinations and transfers](user-guide/destinations-and-transfers.md) | Sending finished recordings somewhere, retries, stopping a transfer |
| [Naming patterns](user-guide/naming-patterns.md) | Naming files and folders with `{date}`, `{machine}`, and the rest |
| [SharePoint setup](user-guide/sharepoint-setup.md) | The app registration and credential a SharePoint destination needs |
| [Command line](user-guide/command-line.md) | Every `captr` command, its options, and its exit codes |
| [Scheduled recording](user-guide/scheduling.md) | Task Scheduler — **read the session 0 warning** |
| [Troubleshooting](user-guide/troubleshooting.md) | The Diagnostics page, common problems, support bundles |

## [Developer guide](developer-guide/) — building and changing Captr

For anyone working on the source.

| | |
|---|---|
| [Architecture](developer-guide/architecture.md) | How the pieces fit together, and one recording end to end |
| [Environment setup](developer-guide/environment-setup.md) | What you need installed, and the first build |
| [Building](developer-guide/building.md) | The build pipeline, its gates, and how to run just one part |
| [Testing](developer-guide/testing.md) | The test suite, its categories, and what covers which requirement |
| [Releasing](developer-guide/releasing.md) | Versioning, signing, the installer, tags, and GitHub releases |
| [Upgrading FFmpeg](developer-guide/upgrading-ffmpeg.md) | Moving to a newer pinned FFmpeg build safely |
| [Design decisions](developer-guide/design-decisions.md) | Where Captr departs from the specification, and why |
| [Release verification](developer-guide/release-verification.md) | The manual checks a release needs on real machines |

## Legal

[Bundled FFmpeg: licence obligations and source offer](ffmpeg-source-offer.md) sits
at the top of `docs/` on purpose — it is a licence obligation that ships with the
product, not guidance for either audience, and build tooling references it by path.
