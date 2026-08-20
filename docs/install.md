# Installing, upgrading, and uninstalling Captr

One signed `.exe` installer built with Inno Setup, containing the application, the
bundled FFmpeg, the self-contained .NET runtime, and licence texts. Per-machine by
default; nothing else is installed — **no scheduled task, no service, no resident
agent** (the application starts on demand and exits when idle).

## Silent / unattended installation (Intune, SCCM, PDQ)

```bat
captr-setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES
```

Useful switches:

| Switch | Effect |
|---|---|
| `/SILENT` / `/VERYSILENT` | Progress-only / fully silent. |
| `/CURRENTUSER` | Per-user install (no elevation) instead of per-machine. |
| `/MERGETASKS="!shortcuts"` | Suppress the Start Menu shortcut. |
| `/WORKINGFOLDER="D:\Recordings"` | Preset the working folder. Applies to the installing user's settings (other users on a per-machine install keep defaults until they choose). |
| `/FORCESTOP=yes` | If a recording is in progress: stop it, wait for it to **finalise**, then proceed. Without this the installer refuses. |
| `/ALLOWDOWNGRADE=yes` | Permit installing an older version over a newer one (refused otherwise). |
| `/LOG="path"` | Write a setup log. |

The installer adds the install folder to `PATH` (machine PATH for per-machine,
user PATH for per-user) with a duplicate check, so upgrades never stack entries.
Open a **new** shell after installing for `captr` to resolve.

Preflight: the installer refuses on non-x64 machines and Windows older than
10.0.17763, and `captr` warns at first start if no hardware encoder passes its
trial (recording still works via the software encoder).

## Upgrading

Run the newer installer over the existing installation:

- Refuses while a recording is in progress unless `/FORCESTOP=yes` (which
  finalises the recording first).
- Refuses downgrades unless `/ALLOWDOWNGRADE=yes`.
- **Preserves** settings (migrated forward on first run), credentials, the
  delivery queue, and all recordings; a previously interrupted session is still
  recovered by the next host start, and pending deliveries resume.
- Leaves exactly one Programs-and-Features entry and no duplicate PATH entry.
- Re-running the same version repairs the installation.

## Uninstalling

Programs and Features → Captr → Uninstall, or silently:

```bat
"%LOCALAPPDATA%\Programs\Captr\unins000.exe" /VERYSILENT     (per-user)
"C:\Program Files\Captr\unins000.exe" /VERYSILENT             (per-machine)
```

By default the uninstaller **keeps** recordings, settings, and the delivery queue.
Interactive uninstall asks whether to remove them (default: keep); silent
uninstall always keeps. Stored credentials are never removed by the uninstaller —
remove them explicitly with `captr auth delete <name>` beforehand if desired.

## Knowing what you have

```
captr version
```

reports the application version, the source commit it was built from, the exact
bundled FFmpeg build, and the .NET runtime. The same block appears on the UI's
Diagnostics page, and the matching `release.json` beside the installer records the
identical identifiers plus file hashes.

## Verifying an installation

```powershell
pwsh build/verify-install.ps1              # from a source tree; add
                                           # -SkipSignatureCheck for unsigned builds
```

Checks binaries + signatures, CLI response and PATH, then performs a real
15-second recording and confirms a correctly-named, playable file reaches a
temporary folder destination.
