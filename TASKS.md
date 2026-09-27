# Release-readiness checklist

The source of truth for what is left. One line per item, ticked when done and
verified. New discoveries are added where they belong. IDs match the findings table
in `REVIEW.md`; `=` marks the same defect reported by more than one reviewer.
Severity is the one assigned after checking the reviewer's evidence against the code.

## Baseline

- [x] Fresh clone: `build.ps1 -Installer` green (format, licence, build, 329 unit, 13 Ffmpeg+Chaos, 9 Published, installer)
- [x] Baseline of the categories the default build skips: 31/31 (Display, Gpu, Soak on this machine's NVENC GPU; 14 uncategorised)

## Review (six areas, every finding checked against the code)

- [x] Recording lifecycle and FFmpeg process handling (REC)
- [x] Transfers, SharePoint, secrets, diagnostics (XFR)
- [x] CLI, IPC, hosting, settings, naming (CLI)
- [x] Installer, packaging, build and release tooling (BLD)
- [x] UI, tray, hotkeys, accessibility (UI)
- [x] Cross-cutting security and threat model (SEC)

## Critical and High — every one fixed with a regression test

- [x] TST-1 14 integration tests have no category and never run in build.ps1 or CI
- [x] SEC-1 (=CLI-5, CLI-6, SEC-6) pipe can be squatted by another user; client never checks the server's owner
- [x] SEC-3 (=BLD-4, REC-8, REC-15, SEC-4, BLD-19) FFmpeg search climbs to `C:\tools`; no runtime hash check; adoption trusts journal image path
- [x] SEC-7 (=CLI-3) session 0 records black and reports success
- [ ] BLD-1 elevated setup runs `captr.exe` from a path read from HKCU
- [ ] BLD-2 `/VERYSILENT` upgrade blocks on an invisible prompt without `/SUPPRESSMSGBOXES`
- [x] CLI-1 (=UI-6) two concurrent starts create two sessions; the first becomes unstoppable
- [x] CLI-2 `start` while the previous recording finalises reports success and records nothing
- [x] CLI-4 (=UI-4, XFR-11 part) settings file with a wrong-typed value or newer schema kills the host and the UI
- [ ] REC-1 an exception in the session loop skips finalisation silently and can leave FFmpeg unsupervised (host now logs and reports "faulted"; session loop itself still to harden)
- [ ] REC-2 (=REC-10, CLI-24) message-only window never receives sleep/display/logoff broadcasts
- [ ] REC-3 settings changes made while paused are dropped (an excluded display is recorded)
- [ ] REC-4 topology rebuild re-includes a display excluded mid-session
- [ ] REC-5 software-fallback arguments are frozen at start (wrong displays, fps, segment group)
- [ ] REC-6 `captr recover` during a recording adopts and kills the live encoder
- [ ] REC-7 finalisation needs a second full copy of the footage; fails exactly when the disk is low
- [x] REC-9 execution state is per-thread; the display-required request can vanish mid-recording
- [x] XFR-1 (=REC-13) UNC folder destinations (and UNC working folders) always fail in `DriveInfo`
- [x] XFR-2 any unexpected exception kills the transfer worker for the life of the host, row stuck in-progress
- [x] XFR-3 an expired or completed upload session never recovers; Retry reuses the dead URL
- [ ] UI-1 (=XFR-5) no crash handler; several UI actions terminate the app
- [ ] UI-2 a failed recording is never shown (host side done: status carries LastOutcome; UI to show it)
- [ ] UI-3 Delete is offered on the live recording and destroys its closed segments

## Medium — fixed where low-risk

- [ ] SEC-2 session folders inherit "Authenticated Users: Modify" under a drive root
- [ ] SEC-8 (=BLD-3) setup kills every user's host and checks recording state as the wrong user
- [ ] SEC-9 retention matches sessions by string prefix and can delete an un-transferred folder
- [ ] BLD-5 PATH change not broadcast (`ChangesEnvironment`)
- [ ] BLD-6 PFX password on signtool's command line
- [ ] BLD-7 signing env var names disagree across scripts and docs
- [ ] BLD-8 signing covers 4 files, no uninstaller, no verification, `signed` flag is env-var presence
- [ ] BLD-10 `verify-install.ps1` edits the wrong settings file and never restores it
- [ ] BLD-11 `/WORKINGFOLDER` trailing backslash breaks the quoted argument
- [ ] BLD-13 PDBs, XML docs shipped; builds not reproducible (paths, timestamps)
- [ ] BLD-16 third-party notices and the source offer not shipped; no contact for the offer
- [x] CLI-7 idle-exit races; a host that loses the mutex race exits immediately
- [ ] CLI-8 `settings init` defeats roaming adoption and `.bak` recovery
- [ ] CLI-9 (=SEC-11) `settings import` bypasses the recording lock and says nothing about what it changed
- [ ] CLI-10 unbounded `retentionDays` stops the host from starting
- [ ] CLI-11 `--fps` not validated
- [x] CLI-12 `start` after a crash times out while recovery runs
- [ ] CLI-13 several commands leak stack traces and emit no JSON on errors
- [ ] CLI-14 one naming failure silently cancels every transfer for the recording
- [ ] REC-11 slow-encoding warning fires once per session
- [ ] REC-12 kill without waiting for exit; `MainModule` read races process start
- [x] XFR-4 Test connection passes a wrong secret from the MSAL token cache
- [ ] XFR-6 support bundle skips the live host log
- [ ] XFR-7 support bundle: user/machine/path not redacted, user not told what it contains, over-redaction
- [ ] XFR-8 Send again twice queues duplicate uploads
- [x] XFR-9 Stop can be overwritten by the worker; timer callback can crash the host
- [ ] XFR-10 SharePoint upload verified by size only
- [x] XFR-11 Diagnostics reports invalid settings as OK and is not read-only
- [ ] UI-5 status poll loop dies permanently on an unexpected exception
- [ ] UI-7 hotkeys: not re-registered on save, not validated, self-clash misreported, Shift+letter accepted
- [ ] UI-8 Tab is trapped in the hotkey box and clears the hotkey
- [ ] UI-9 adding a destination with a duplicate name overwrites the other's secret
- [ ] UI-10 libx264 labelled "Hardware accelerated"
- [ ] UI-11 Reset to defaults: no confirmation, bypasses lock, orphans secrets
- [ ] UI-12 Transfers action errors never shown; queue construction can crash startup

## Low — fixed if trivial, otherwise recorded with a recommendation

- [ ] SEC-5 re-send/verify trust `integrity.json` file names
- [~] SEC-10 upload URL now must be https (fixed); certificate credential unsupported (deferred)
- [x] SEC-12 `.partial` opened with FileMode.Create in shared folders
- [ ] REC-14 finalisation picks up leftover `.repaired.mkv` files
- [ ] REC-16 dead overlay path with a latent `%` escaping bug; other dead parameters
- [ ] REC-17 chaos test assertion always true
- [ ] REC-18 disk-guard double-counts the ballast
- [x] XFR-12 folder copy not flushed to disk; `.partial` left on cancel
- [ ] XFR-13 renaming a destination deletes its old secret before save
- [ ] XFR-14 no certificate credential (see SEC-10)
- [x] XFR-15 408/423 treated as permanent; Retry-After dates ignored
- [x] XFR-16 no worker-level transfer tests
- [ ] CLI-15 `resend` relative path resolved in the host's directory
- [x] CLI-16 retry/stop report success for unknown ids
- [x] CLI-17 pause/resume report a state they did not check
- [ ] CLI-18 reserved names incomplete; not applied to folder segments
- [ ] CLI-19 destination folder validation accepts relative/ADS paths
- [x] CLI-20 client leaks a pipe handle on a failed handshake
- [~] CLI-21 malformed envelope now logged (fixed); IPC request timeouts (open)
- [ ] CLI-22 Ctrl+C exit code 130 undocumented
- [ ] CLI-23 masked secret entry: non-BMP, unwiped buffers, no console
- [ ] UI-13 Transfers refresh pile-up and focus loss
- [ ] UI-14 display preview caches a failure; keeps full-res bitmaps
- [ ] UI-15 starting/suspended states fall through to Idle
- [ ] UI-16 second launch cannot bring the window to the front
- [ ] UI-17 dead code and stale comments in the App
- [ ] BLD-12 uninstall has no running-recording check
- [ ] BLD-14 pre-release versions accepted but break the build
- [ ] BLD-15 docs call the GPL FFmpeg build LGPL
- [ ] BLD-17 CI: actions not SHA-pinned, no permissions block, no timeout
- [x] BLD-18 normalise-line-endings regex never matches Windows paths; BOMs committed
- [ ] BLD-20 release script details (local-only tag check, notes not attached, Inno not re-verified)
- [ ] DOC-1 `design-decisions.md` has contradictory tray-icon entries; docs disagree on blinking
- [ ] DOC-2 `architecture.md` says settings are in `%APPDATA%`

## Code signing

- [ ] Pinned, hash-verified `signtool` fetched into `tools/` (no SDK install needed)
- [ ] `build/Sign-Artifacts.ps1`: Pfx / CertStore / ArtifactSigning, SHA-256, RFC 3161 with fallback servers, `-DryRun`
- [ ] FFmpeg binaries signed only after their pinned hash is verified
- [ ] Inno `SignTool` + signed uninstaller
- [ ] `build/Verify-Signatures.ps1`: unsigned / untimestamped / wrong subject fail the build
- [ ] Wired into `build.ps1` / `make-installer.ps1` / `new-release.ps1` / CI
- [ ] Tests for both scripts (dry run with no certificate; verify failure modes)
- [ ] `releasing.md`: setup, CI secret names, local use, troubleshooting, SmartScreen note

## End-to-end

- [ ] Hermetic data root so E2E tests never touch the developer's real settings
- [ ] install → first run → record → transfer → uninstall
- [ ] upgrade from the previous release
- [ ] every CLI command and every documented exit code
- [ ] failure paths: disk full, missing/tampered FFmpeg, FFmpeg crash, unreachable destination, bad credentials, invalid naming pattern, session 0
- [ ] `testing.md` requirement-coverage map updated

## UX (targeted; existing style kept)

- [ ] First run: default folder shown, quick test recording
- [ ] Recording state always visible; unmistakable failure signal (UI-2)
- [ ] Plain-language errors with a next step
- [ ] Transfer progress, retries and queue visible outside the Transfers page
- [ ] Naming-pattern live preview that flags invalid output
- [ ] Hotkey conflict detection (UI-7)
- [ ] Accessibility: screen-reader names, live regions, keyboard (UI-8), high contrast
- [ ] Wording consistent across UI, CLI help and user guide

## Production readiness

- [ ] Version stamping verified in every binary and the installer
- [ ] PDBs archived, not shipped (BLD-13)
- [ ] Crash handler writes a useful local report (UI-1)
- [x] Logs capped and rotated (20 MB per file, 14 files)
- [ ] Licence and third-party notices shipped (BLD-16)
- [ ] Reproducible, signed, verified release artifacts

## Documentation and report

- [ ] `docs/user-guide/` updated for behaviour changes
- [ ] `docs/developer-guide/` updated for process changes
- [ ] `docs/developer-guide/release-verification.md` manual checks
- [ ] `REVIEW.md`
