# Installing, upgrading, and uninstalling

One `.exe` installer containing the application, the command line, the bundled
FFmpeg, the .NET runtime, and the licence texts. Nothing needs to be installed
first.

**Nothing else is installed** — no service, no scheduled task, no resident agent,
no browser extension, no startup entry. Captr runs when you run it.

## Installing

Run `captr-setup-<version>.exe` and follow the wizard. Two choices are worth a
moment:

| Option | Default | What it does |
|---|---|---|
| Create a Start Menu shortcut | on | The usual shortcut. |
| Add Captr to my PATH | on | Lets you type `captr` in any terminal. Written to **your** PATH, not the machine's, so it needs no extra permission and is removed again when you uninstall. |

By default Captr installs for everyone on the machine (`C:\Program Files\Captr`),
which needs administrator rights once, at install time. Recording itself never
needs elevation.

> After installing, open a **new** terminal window before typing `captr`. An
> already-open terminal has the old PATH.

## Silent and unattended installation

For Intune, SCCM, PDQ, or any script:

```bat
captr-setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES
```

| Switch | Effect |
|---|---|
| `/SILENT` | Progress bar only, no questions. |
| `/VERYSILENT` | Nothing on screen at all. |
| `/CURRENTUSER` | Install for the current user only (`%LOCALAPPDATA%\Programs\Captr`). No elevation needed. |
| `/MERGETASKS="!shortcuts"` | Do not create the Start Menu shortcut. |
| `/MERGETASKS="!addtopath"` | Do not touch PATH. |
| `/WORKINGFOLDER="D:\Recordings"` | Preset where recordings are written. Applies to the installing user; other users keep the default until they change it. |
| `/FORCESTOP=yes` | If a recording is in progress, stop it, **wait for it to finalise**, then install. Without this the installer refuses. |
| `/ALLOWDOWNGRADE=yes` | Permit installing an older version over a newer one. Refused otherwise. |
| `/LOG="C:\temp\captr-setup.log"` | Write a setup log. |

Switches can be combined:

```bat
captr-setup-0.1.0.exe /VERYSILENT /MERGETASKS="!shortcuts" /WORKINGFOLDER="D:\Recordings"
```

## What the installer checks before it starts

- **64-bit Windows 10 1809 (build 17763) or newer.** Older or 32-bit is refused
  with a message rather than installed and broken.
- **Nothing is recording.** Installing over a live recording would kill it
  mid-file. Use `/FORCESTOP=yes` to stop and finalise it first.
- **Whether Captr is already installed**, and what you are about to do to it:

  | Situation | What you are asked |
  |---|---|
  | Nothing installed | Nothing — an ordinary first install. |
  | The **same** version | "Captr *x.y.z* is already installed… Reinstall?" Reinstalling repairs a damaged installation and keeps everything. |
  | An **older** version | "Captr *x* is installed. This will upgrade it to *y*." Your recordings, settings, credentials, and pending transfers are all kept. |
  | A **newer** version | A warning: settings are migrated **forward only**, so a file written by the newer version may not load in the older one and Captr would start from defaults. Defaults to **not** downgrading. |

  Silent installs are not blocked by these: they take the default answer, so
  `/VERYSILENT` still reinstalls and upgrades unattended. A downgrade still needs
  `/ALLOWDOWNGRADE=yes`.

## Your settings on a fresh machine

A first install creates `settings.json` with sensible defaults, so Captr is ready to
record immediately rather than waiting for you to open Settings and save something.

An install that finds an existing settings file **never overwrites it** — that is
what makes an upgrade, a repair, and a reinstall-after-uninstall all keep your
configuration.

`/WORKINGFOLDER="D:\Recordings"` presets the working folder in that first file.

## Upgrading

Run the newer installer over the existing installation. It:

- **keeps** your settings (migrated forward automatically), your stored
  credentials, the transfer queue, and every recording;
- resumes transfers that had not finished;
- still recovers a recording that was interrupted before the upgrade;
- leaves exactly one entry in Programs and Features, and one PATH entry.

Re-running the same version repairs the installation.

## Uninstalling

**Settings → Apps → Captr → Uninstall**, or:

```bat
"C:\Program Files\Captr\unins000.exe" /VERYSILENT
"%LOCALAPPDATA%\Programs\Captr\unins000.exe" /VERYSILENT
```

After confirming you want to remove Captr, the uninstaller asks one question with
two clearly labelled choices:

- **Keep my recordings and settings** — everything under `%LOCALAPPDATA%\Captr`
  stays. A later reinstall picks up exactly where you left off: same working folder,
  same destinations, same hotkeys.
- **Delete everything Captr has stored** — asks once more, because deleting
  recordings cannot be undone.

**Keeping is the default**, and a silent uninstall always keeps.

Stored SharePoint secrets are not removed by the uninstaller. Remove them first if
you want them gone:

```bat
captr auth delete "Captr:my-destination"
```

or delete the destination in Settings, which does the same thing.

### What uninstalling removes
- The program files, the Start Menu entry, and Captr's entry on your PATH.
- Captr's listing under **Settings > Personalisation > Taskbar > Other system tray
  icons**. Windows keeps that listing for every program that has ever shown a tray
  icon and never removes it by itself, so an uninstalled Captr would otherwise be
  offered there for ever.
- Your recordings and settings only if you say so — you are asked, and the default is
  to keep them. Kept settings are picked up again by a later reinstall.

Nothing is ever removed from a destination. Recordings that have been transferred
somewhere stay there.

## Checking what you have installed

```bat
captr version
```

reports the application version, the exact source commit it was built from, the
bundled FFmpeg build, and the .NET runtime. The same four lines appear on the
**Diagnostics** page. Quote them when reporting a problem.

Every release also publishes `SHA256SUMS.txt` and `release.json` beside the
installer, so you can verify a download and see exactly what went into it.
