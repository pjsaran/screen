# Installing, upgrading, and uninstalling

One `.exe` installer containing the application, the command line, the bundled
FFmpeg, the .NET runtime, and the licence texts. Nothing needs to be installed
first.

In a signed release, every executable and DLL it installs carries a digital
signature, and so do the installer and the uninstaller, so Windows names Captr's
publisher instead of "Unknown publisher". Debugging symbols (`.pdb` files) are not
installed. The licence and third-party notices are in the `licenses` folder beside
the program (`THIRD-PARTY.md`, the FFmpeg source offer, and the .NET runtime's
licence and notices), with FFmpeg's own licence texts in `ffmpeg\licenses`.

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
> already-open terminal has the old PATH. You do not need to sign out: the installer
> tells Windows the PATH changed, so a terminal opened afterwards finds `captr`.

## Silent and unattended installation

For Intune, SCCM, PDQ, or any script:

```bat
captr-setup-<version>.exe /VERYSILENT
```

A silent install **never waits for anyone**, with or without `/SUPPRESSMSGBOXES`:
there is no question it can stop on, and anything it would have said in a message
box goes to the setup log instead. It either finishes or exits with a code that says
why not (see [exit codes](#exit-codes) below). That matters most where it is hardest
to see — a deployment tool running setup with nobody signed in.

| Switch | Effect |
|---|---|
| `/SILENT` | Progress bar only, no questions. |
| `/VERYSILENT` | Nothing on screen at all. |
| `/CURRENTUSER` | Install for the current user only (`%LOCALAPPDATA%\Programs\Captr`). No elevation needed. |
| `/MERGETASKS="!shortcuts"` | Do not create the Start Menu shortcut. |
| `/MERGETASKS="!addtopath"` | Do not touch PATH. |
| `/WORKINGFOLDER="D:\Recordings"` | Preset where recordings are written, when setup creates the installing user's settings file. Other users keep the default until they change it. A trailing backslash (`"D:\Recordings\"`) is fine. |
| `/FORCESTOP=yes` | If a recording is in progress, stop it, **wait for it to finalise**, then install. Also closes Captr where it is running for **another account** on the PC — a per-machine (elevated) setup can do that, a `/CURRENTUSER` one cannot — and that person's recording, if there was one, is recovered the next time they start Captr. Without this switch setup refuses in both cases. |
| `/ALLOWDOWNGRADE=yes` | Permit installing an older version over a newer one. Refused otherwise. |
| `/LOG="C:\temp\captr-setup.log"` | Write a setup log. Every refusal is explained there. |

Switches can be combined:

```bat
captr-setup-0.1.0.exe /VERYSILENT /MERGETASKS="!shortcuts" /WORKINGFOLDER="D:\Recordings"
```

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Installed. |
| `1` | Refused before anything was changed: a recording is in progress (and `/FORCESTOP=yes` was not given, or the recording could not be stopped cleanly within about two minutes), or this would be a downgrade without `/ALLOWDOWNGRADE=yes`. Interactively, it is also what answering No gives. |
| `7` | Could not prepare to install: Captr is running for another account on this PC, and `/FORCESTOP=yes` was not given. |

These are Inno Setup's own codes for "setup could not initialise" and "the
preparing-to-install step failed"; the setup log (`/LOG=`) gives the reason in
words.

## What the installer checks before it starts

- **64-bit Windows 10 1809 (build 17763) or newer.** Older or 32-bit is refused
  with a message rather than installed and broken.
- **Nothing is recording.** Installing over a live recording would kill it
  mid-file. Use `/FORCESTOP=yes` to stop and finalise it first. Setup asks as the
  person who started it — the only account whose recorder it can see — and never
  runs the installed `captr` with administrator rights.
- **Captr is not running for someone else.** Setup closes your own Captr (the window
  and an idle recorder) before replacing its files. If Captr is still running for
  another account on the PC it may be recording, so setup stops and says so (exit
  `7`) unless `/FORCESTOP=yes` is given.
- **Whether Captr is already installed**, and what you are about to do to it:

  | Situation | What you are asked |
  |---|---|
  | Nothing installed | Nothing — an ordinary first install. |
  | The **same** version | "Captr *x.y.z* is already installed… Reinstall?" Reinstalling repairs a damaged installation and keeps everything. |
  | An **older** version | "Captr *x* is installed. This will upgrade it to *y*." Your recordings, settings, credentials, and pending transfers are all kept. |
  | A **newer** version | A warning: settings are migrated **forward only**, so a file written by the newer version may not load in the older one and Captr would start from defaults. Defaults to **not** downgrading. |

  Silent installs (`/SILENT` or `/VERYSILENT`) are never asked: a reinstall or an
  upgrade simply proceeds, and a downgrade is refused — written to the setup log,
  exit `1` — unless `/ALLOWDOWNGRADE=yes` is given.

  A Captr that finds a settings file written by a newer version does not throw it
  away: it keeps it, untouched, as `settings.json.from-schema-<N>` beside
  `settings.json`, starts from defaults, and says so on the Diagnostics page.
  Installing the newer version again and putting that file back as `settings.json`
  restores everything.

## Your settings on a fresh machine

A first install creates `settings.json` with sensible defaults, so Captr is ready to
record immediately rather than waiting for you to open Settings and save something.

An install that finds an existing settings file **never overwrites it** — that is
what makes an upgrade, a repair, and a reinstall-after-uninstall all keep your
configuration.

`/WORKINGFOLDER="D:\Recordings"` presets the working folder in that first file; an
existing settings file is left alone, switch or not. The file is created as the
person who ran setup, not as the administrator account that elevated it, so it lands
where that person's Captr looks. If creating it fails, setup logs why and carries on,
and Captr creates its settings on first use.

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

**The uninstaller refuses while a recording is in progress**, because removing the
program under a running recorder would cut the recording off. Stop it first, or run
the uninstaller with `/FORCESTOP=yes`, which stops the recording, waits for it to be
finalised, and then uninstalls. The check asks the recorder of the account running
the uninstaller.

After confirming you want to remove Captr, the uninstaller asks one question with
two clearly labelled choices:

- **Keep my recordings and settings** — everything under `%LOCALAPPDATA%\Captr`
  stays. A later reinstall picks up exactly where you left off: same working folder,
  same destinations, same hotkeys.
- **Delete everything Captr has stored** — asks once more, because deleting
  recordings cannot be undone.

**Keeping is the default**, and a silent uninstall always keeps. Even "Delete
everything" removes only `%LOCALAPPDATA%\Captr` (and `%APPDATA%\Captr`, where a much
older Captr kept its settings): a working folder you chose elsewhere, such as
`D:\Recordings`, is never touched by the uninstaller.

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
