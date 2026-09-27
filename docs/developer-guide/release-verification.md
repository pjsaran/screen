# Release verification runbook

What must be true before a build ships, split into what CI and the scripts prove
automatically and what needs a person with real machines. Items marked ☐ are the
manual checks: each needs something this repository's automated suites cannot
provide — a real code-signing certificate, real monitors, a real Microsoft 365
tenant, a second Windows account, sleep, or a person listening to a screen reader.
Each gives exact steps and the expected result; a result that differs is a release
blocker until it is explained.

`testing.md` maps every requirement to the test that covers it, and names each
deferred item — read it alongside this checklist.

## Automated (every release build)

`pwsh build/new-release.ps1` runs all of this, and refuses to publish if any of it
fails. `pwsh build/build.ps1 -Installer -RequireSigning` is the same without the
tag and the upload.

- [x] `build/build.ps1`: licence gate (report committed, no disallowed licence,
      transitive included), formatting verified, warnings-as-errors build, unit
      tests, and the integration categories a plain machine can run (`Os`,
      `Ffmpeg`, `Chaos`, and anything uncategorised), then publish, signing, and the
      `Published` tests (the CLI end-to-end suite) against the payload. Add `-Full`
      on a machine with a real desktop and GPU for `Display`, `Gpu` (including the
      recording end-to-end suite), and `Soak`.
- [x] `build/fetch-ffmpeg.ps1`: pinned GPL FFmpeg checksum-verified; `ddagrab`
      and hardware encoders asserted; libopenh264 presence recorded; a
      signature-independent digest of each executable recorded for the runtime check.
- [x] Signing: every shipped `.exe` and `.dll`, FFmpeg only after its pinned digest
      is verified, the installer and uninstaller; RFC 3161 timestamps.
- [x] `build/make-installer.ps1`: installer built with the pinned Inno Setup
      (checked by hash), every shipped file's signature verified
      (`artifacts/signature-report.json`), symbols zip, checksums, and
      `release.json` with `"signed": true` only when verified.
- [x] `new-release.ps1` refuses to start without a signing method and
      `CAPTR_SIGN_PUBLISHER`, and refuses to tag unverified artefacts.

Run on purpose before a release, on the build machine:

- [ ] The installer suite: `dotnet test tests/Captr.Integration.Tests -c Release
      --no-build --filter "Category=Installer"`, with `CAPTR_PREVIOUS_INSTALLER` set
      to the previous release's setup program (see
      [Testing](testing.md#the-installer-suite)).
- [ ] `build/verify-install.ps1` after installing the release: install → CLI
      answers → real 15 s recording → playable, correctly-named file transferred to
      a folder destination, in a data root of its own.

## Signing with the real certificate

☐ **Sign, verify, install, and look at what Windows shows.**

1. In a fresh terminal, configure the real method (for example
   `$env:CAPTR_SIGN_CERT_THUMBPRINT`, or the Pfx or Artifact Signing variables — see
   [Releasing](releasing.md#code-signing)) and `$env:CAPTR_SIGN_PUBLISHER` to the
   certificate's CN.
2. `pwsh build/Sign-Artifacts.ps1 -Path publish -DryRun` — expected: the plan
   names the method and lists Captr's files, FFmpeg, and the unsigned third-party
   DLLs to sign; "FFmpeg digests verified".
3. `pwsh build/build.ps1 -Installer -RequireSigning` — expected: succeeds;
   "All N shipped file(s) are signed by an expected publisher with an RFC 3161
   timestamp"; `artifacts/release.json` has `"signed": true` and `"signedBy"` equal
   to the CN.
4. `Get-AuthenticodeSignature artifacts\captr-setup-*.exe, publish\captr.exe,
   publish\Captr.App.exe, publish\Captr.Core.dll, publish\ffmpeg\ffmpeg.exe` —
   expected: `Status: Valid` for every one, signer = the CN, and a
   `TimeStamperCertificate`.
5. On a clean VM, download the installer through a browser (so it carries the Mark
   of the Web) and run it. Expected: the UAC prompt says **Verified publisher:**
   the CN, not "Unknown publisher". SmartScreen may still show "Windows protected
   your PC" for a new certificate or a new file — record what it shows; that is
   reputation, not a signing fault (see
   [Releasing](releasing.md#what-signing-does-and-does-not-buy)).
6. After installing, right-click `C:\Program Files\Captr\Captr.App.exe`, then
   `captr.exe`, `ffmpeg\ffmpeg.exe`, and `unins000.exe` → **Properties → Digital
   Signatures**. Expected: one signature each, by the CN, SHA256, with a timestamp.
   Run `pwsh build/verify-install.ps1` — expected: every check passes, including
   "binaries signed with valid signatures".
7. On a Windows 11 VM with Smart App Control **on**, install and make a recording.
   Expected: nothing is blocked.

## Clean-machine matrix (Win10 + Win11 VMs)

On EACH of a clean Windows 10 (≥17763) and Windows 11 VM:

☐ Silent install (`/VERYSILENT`, without `/SUPPRESSMSGBOXES`) finishes with exit
  code 0 and no window; Start Menu entry present; `captr` resolves in a newly
  opened terminal without signing out.
☐ `verify-install.ps1` passes end to end.
☐ A Task Scheduler task configured **run only when user is logged on** starts and
  stops a recording (the `../user-guide/scheduling.md` example), and the resulting
  file is correct.
☐ Upgrade: install version N-1, configure settings + a credential + start/interrupt
  a session (kill the host mid-recording), queue a transfer to an unreachable
  destination. Install version N: refused while recording unless `/FORCESTOP=yes`;
  afterwards settings/credential preserved, interrupted session recovered on first
  host start, pending transfer completes, one Programs entry, one PATH entry.
☐ Repair: re-run the same version; still works.
☐ Downgrade: refused without `/ALLOWDOWNGRADE=yes` (silently: exit 1, reason in the
  `/LOG`). With it, the newer settings file is kept as
  `settings.json.from-schema-<N>` and Diagnostics says so.
☐ Uninstall: recordings/settings/queue remain by default; removed when "Delete
  everything Captr has stored" is chosen and confirmed; a working folder outside
  `%LOCALAPPDATA%\Captr` is never touched.

## Real hardware

☐ **Multiple monitors, mixed DPI, and changes mid-recording.** Two (better, three)
physical monitors with different resolutions, one at 100 % scaling and one at 150 %.

1. Start a recording with every display included; wait a minute. Expected: Home
   shows each display's thumbnail; `captr status` says `recording`.
2. Plug a USB stick or headset in and out. Expected: nothing happens to the
   recording — no gap, no new segment in `journal.ndjson`.
3. Change one monitor's resolution in Display settings. Expected: one encoder
   restart into a new arrangement, a short gap journaled with its reason — not a
   run of encoder faults and not a stop.
4. Unplug one monitor. Expected: the recording carries on with the remaining
   display(s) after a short gap; plug it back in — it is included again (a returning
   display that was not excluded is recorded).
5. In Settings, untick one display while recording. Expected: accepted and applied
   at once (it is less work); then unplug and replug another display — the
   unticked one stays excluded.
6. Stop. Expected: the recording finalises; each arrangement produced its own output
   file; every display appears at its native resolution (open the files — no
   scaling, no black padding cropping the picture); `captr recordings verify`
   passes; coverage lists each gap with a reason.
7. Reboot, open Captr. Expected: the unticked display is still unticked.

☐ **Sleep and resume during a recording.**

1. Start a recording. Run `powercfg /requests`. Expected: Captr's recorder is listed
   under both DISPLAY and SYSTEM with the reason "Captr is recording the screen".
2. Leave the PC untouched longer than its display-off timeout. Expected: the
   display stays on.
3. Sleep the PC (Start → Power → Sleep). Wake it after two minutes and sign in.
   Expected: the recording carries on without being restarted; while asleep the
   status was `suspended` (the journal shows the suspend and the resume); the sleep
   appears as one gap of about two minutes, with a new segment after resume.
4. Stop, then run `powercfg /requests` again. Expected: Captr is no longer listed.

☐ **Ten-hour soak.** `CAPTR_SOAK_MINUTES=600` with the `Soak` trait — no handle
leak, no unbounded memory growth, wall-vs-encoded drift < 1 s, journal internally
consistent, every segment verifies, coverage > 99.9 %. (Shorter runs of the same
test pass on the development machine; only the clock differs.)

☐ **GPU driver reset under load** (a vendor tool, or `pnputil /restart-device` on
the display adapter mid-recording): the session must gap honestly and continue, or
stop loudly and show as stopped on its own — never hang or record silence.

## Scheduled task in session 0

☐ **A "whether logged on or not" task is refused, not black.** On a real machine:

1. `schtasks /Create /TN "Captr s0" /TR "\"C:\Program Files\Captr\captr.exe\" start" /SC ONCE /ST 23:59 /RU %USERNAME% /RP *`
   (the password prompt makes it "Run whether user is logged on or not").
2. Note what `captr recordings list` shows, then `schtasks /Run /TN "Captr s0"` and
   wait a minute.
3. `schtasks /Query /TN "Captr s0" /V /FO LIST`. Expected: **Last Result: 1**.
4. Expected: `captr recordings list` shows no new recording and the working folder
   has no new session folder; within a few minutes no `Captr.App.exe` remains in
   session 0 (Task Manager → Details, Session ID column).
5. Optional: change the task's action to `cmd /c "...\captr.exe" start 2> C:\temp\s0.txt`
   and run again. Expected: the file says "Captr cannot record here: it is running
   in session 0 …" and names "Run only when user is logged on".
6. `schtasks /Delete /TN "Captr s0" /F`.

## A real SharePoint tenant

☐ Follow `../user-guide/sharepoint-setup.md` against a real tenant, with the app
registration limited to **Sites.Selected** and granted on one site.

1. **Upload.** Add the destination; **Test connection** — expected: passes. Record
   one minute and stop. Expected: the transfer goes QUEUED → SENDING →
   TRANSFERRED, and the file in the library has exactly the local file's size.
2. **Large file resume.** Send a recording of several hundred megabytes (`captr
   recordings resend <folder>` to a fresh destination folder). While SENDING,
   disconnect the network for two minutes, then reconnect. Expected: the transfer
   retries by itself and continues from a confirmed offset rather than from zero
   (its progress bar picks up where it stopped instead of dropping back to the
   start), and the final size matches.
3. **Bad secret.** Pipe a wrong secret over the destination's stored one
   (`"wrong" | captr auth set-secret "<its credential name>"`) and send a recording.
   Expected: SIGN-IN NEEDED, nothing uploaded, Home says "1 transfer needs
   attention — see Transfers". Enter the real secret in the destination and save.
   Expected: the transfer goes back into the queue by itself and completes.
4. **Missing grant.** Add a second destination for a site the app was **not**
   granted. Send a recording. Expected: REFUSED, with SharePoint's own
   `accessDenied` text under it, no automatic retries, the local recording
   untouched. Grant the site, press Retry — expected: TRANSFERRED.
5. Export a support bundle. Expected: no tenant id, client id, or SharePoint host
   anywhere in it (placeholders instead), and no secret or token.

## Installing for everyone, with more than one account

☐ Two accounts on one PC: **A**, an administrator, and **B**, a standard user.
Install the previous release per-machine first.

1. Sign in as B, open Captr, start a recording. Switch user (not sign out) to A.
2. As A, run the new installer elevated and silently:
   `captr-setup-<version>.exe /VERYSILENT /LOG=C:\temp\s1.log`. Expected: exit
   code **7** within seconds; the log says Captr is running for another account;
   switch back to B — B's recording is still running.
3. As A again: `... /VERYSILENT /FORCESTOP=yes /LOG=C:\temp\s2.log`. Expected: exit
   code 0; B's Captr processes were closed. Switch to B and open Captr. Expected:
   the interrupted recording is recovered and finalised on start;
   `captr recordings verify` on it passes.
4. As A, start a recording yourself, then run the installer silently without
   `/FORCESTOP`. Expected: exit code **1**, "A recording is in progress" in the log,
   A's recording still running. With `/FORCESTOP=yes`: A's recording is stopped and
   finalised first, then the install completes; the recording verifies.
5. As B (standard user), run the installer and, at UAC, enter A's credentials.
   Expected: the settings and working-folder check are B's — B's settings file is
   created or kept in **B's** profile, and a recording of B's refuses the install.
6. Uninstall while A is recording: `"C:\Program Files\Captr\unins000.exe"
   /VERYSILENT`. Expected: refused, nothing removed, the recording continues. With
   `/FORCESTOP=yes`: the recording is finalised, then Captr is uninstalled, and the
   recording and settings remain.

## Accessibility

☐ **High contrast.** Turn on a contrast theme (Settings → Accessibility → Contrast
themes → Aquatic), then start Captr (the theme is read at start). Visit Home,
Recordings, Transfers, Settings, Diagnostics, and Help. Expected: every text, button
outline, focus rectangle, and status is visible in the theme's colours; no state is
shown by colour alone; the tray icons remain distinguishable.

☐ **Narrator walkthrough** (Ctrl+Win+Enter), keyboard only:

1. Home — Tab to **Start recording**; with no recordings yet, **Record a 10-second
   test recording** and **Open the folder recordings are saved to** are announced.
   Run the test; the result sentence is read.
2. Recordings — each row's buttons are announced with the recording's name ("Show
   … in File Explorer", "More actions for …"), not just "button".
3. Settings — the naming pattern box is announced as "Naming pattern" and its
   preview is read as you type; the hotkey boxes are announced as "Start or stop
   recording hotkey" / "Pause or resume hotkey", and **Tab moves out of them**
   without clearing the hotkey; each destination's **Edit destination …** / **Remove
   destination …** names the destination; in the destination editor, the folder and
   file-name boxes are named and their previews read.
4. Transfers — with a few rows present, **Retry sending …** / **Stop sending …**
   name the file; wait through several refreshes (every 3 s): Narrator's position
   and keyboard focus stay where they are.

## Hotkeys

☐ **Another application owns Ctrl+Alt+F9.** In a PowerShell window, hold the
combination:

```powershell
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool RegisterHotKey(System.IntPtr h, int id, uint mods, uint vk);'
[W.U]::RegisterHotKey([System.IntPtr]::Zero, 1, 3, 0x78)   # Ctrl+Alt+F9 -> True
Read-Host 'Holding Ctrl+Alt+F9; press Enter to release'
```

1. Start Captr. Expected: a "Hotkey conflicts" message naming Ctrl+Alt+F9 and
   "start/stop recording" as used by another application.
2. Settings → Save. Expected: the same message beside the hotkey boxes.
3. Change start/stop to Ctrl+Alt+F8 and Save. Expected: the message goes; pressing
   Ctrl+Alt+F8 starts a recording at once, with no restart.
4. Try to set Shift+A, and then the pause hotkey equal to the start/stop one.
   Expected: both refused, the box keeps its previous value, and the reason is
   given. F7 alone is accepted.

## First run

☐ **"Record a test" in the window.** On a PC (or `CAPTR_DATA_ROOT`) with no
recordings yet:

1. Open Captr. Expected: Home says where recordings are saved and offers **Record a
   test** and **Open folder**; Open folder opens that folder.
2. Press **Record a test**. Expected: a note that the first recording checks which
   encoder works; after about ten seconds, "Test recording OK: 10 s with <encoder>,
   <size>" (or the failure with its reason).
3. Recordings shows the test (labelled `test`); it plays. Delete it. Expected: the
   first-run card does not return once a real recording exists.

## Support bundle

☐ **Read what would be sent.** With a recording running, and at least one
SharePoint and one UNC folder destination configured, Diagnostics → **Export support
bundle**.

Expected: the window says what the bundle holds. Open the zip: `README.txt` lists
every file (and anything unreadable); today's `host-*.log` is present even though
the recorder is running; crash reports, journals, integrity records and
`system-info.txt` are there; no `.mkv` or any other video. Search the whole zip for
your Windows user name, PC name, profile path, tenant id, client id, SharePoint
host, and UNC server name — expected: none found, placeholders instead
(`<user>`, `<pc>`, `%USERPROFILE%`, `<tenant-id>`, `<client-id>`,
`<sharepoint-host>`, `<server>`). Search for the client secret and for `eyJ` —
expected: none. FFmpeg command lines are readable (`-force_key_frames` not masked).

## Picture quality

Quality is a **user setting**, not a release gate. Three independent controls —
the frame rate, the speed preset (`ultrafast` … `fast`), and the quality level
(`lossless`, `maximum`, `high`, `balanced` default, `compact`) — are adjustable at
any time from the UI or `captr settings set`. A reviewer who wants sharper small
text raises the quality; one who wants smaller files chooses a slower preset or a
lower frame rate. Nothing in the build needs to change. What the build
DOES guarantee is that the chosen settings are the ones actually used and
reported (encoder selection never silently substitutes, and the status view names
the encoder actually running).
