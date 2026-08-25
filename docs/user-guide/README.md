# Captr user guide

Captr records your screen to a file. It is built around one promise: **whatever was
recorded before something went wrong is still there afterwards.** A crash, a power
cut, a full disk, or an unplugged monitor costs you seconds, not the session.

## Start here

1. **[Installation](installation.md)** — get it on the machine.
2. **[Getting started](getting-started.md)** — make your first recording.
3. **[Recording](recording.md)** — the three quality choices and what they cost.

## Then, when you need it

- **[Destinations and transfers](destinations-and-transfers.md)** — have finished
  recordings copied to a folder, a network share, or SharePoint automatically.
- **[Naming patterns](naming-patterns.md)** — control what files and folders are
  called, including dated folders.
- **[SharePoint setup](sharepoint-setup.md)** — the one-time app registration.
- **[Command line](command-line.md)** — `captr` does everything the window does.
- **[Scheduled recording](scheduling.md)** — record on a timetable. **Read the
  session 0 warning before you build a task**; it is the single mistake that
  produces hours of black video.
- **[Troubleshooting](troubleshooting.md)** — what to check, in what order.

## Things worth knowing before you start

**The window is only a viewer.** Recording happens in a separate background
process. Closing the Captr window, or even ending it in Task Manager, does not stop
a recording. Reopening it reattaches to whatever is in progress.

**Captr installs no service, no scheduled task, and no resident agent.** It runs
when you run it and exits when it has nothing to do. Nothing of Captr is running
right now unless you started it.

**Nothing is uploaded anywhere unless you configure a destination**, and there is
no telemetry of any kind.

**Recordings are Matroska (`.mkv`) files.** Windows Media Player, VLC, and every
current editor open them. The container is chosen because it survives being cut off
mid-write, which is the whole point.
