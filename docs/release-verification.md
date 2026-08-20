# Release verification runbook

What must be true before a build ships, split into what CI/scripts prove
automatically and what needs a human with real machines. Items marked ☐ are the
deferred checks for environments this repository's development machine cannot
provide (clean VMs, a real code-signing certificate, a real Microsoft 365 tenant).

`docs/test-coverage.md` maps every SPEC §14 requirement to the test that covers it,
and names each deferred item — read it alongside this checklist.

## Automated (every release build)

- [x] `build/build.ps1`: formatting verified, warnings-as-errors build, unit +
      CI-safe integration tests, licence gate (report committed, no disallowed
      licence, transitive included).
- [x] `build/fetch-ffmpeg.ps1`: pinned LGPL FFmpeg checksum-verified; `ddagrab`
      and hardware encoders asserted; libopenh264 presence recorded.
- [x] `build/make-installer.ps1`: installer built from pinned Inno Setup,
      checksums + `release.json` emitted.
- [x] `build/verify-install.ps1` on the build machine: install → CLI answers →
      real 15 s recording → playable, correctly-named file delivered to a folder
      destination.

## Signing (needs the real certificate)

☐ Set `CAPTR_SIGN_PFX` / `CAPTR_SIGN_PFX_PASSWORD` (+ optional
  `CAPTR_SIGN_TIMESTAMP_URL`) and rebuild: `Captr.App.exe`, `captr.exe`,
  `ffmpeg.exe`, `ffprobe.exe`, and the installer must all show
  `Get-AuthenticodeSignature … Status: Valid` with an RFC 3161 timestamp.
  (The signing path itself is exercised unsigned-warning + self-signed in
  development; only the certificate differs.)

## Clean-machine matrix (needs Win10 + Win11 VMs)

On EACH of a clean Windows 10 (≥17763) and Windows 11 VM:

☐ Silent install (`/VERYSILENT`) succeeds; Start Menu entry present; `captr`
  resolves in a new shell.
☐ `verify-install.ps1` passes end-to-end.
☐ A Task Scheduler task configured **run only when user is logged on** starts and
  stops a recording (docs/task-scheduler.md example), and the resulting file is
  correct. Then reconfigure the same task "whether user is logged on or not" and
  observe the recording is black/unusable — confirming the documented warning.
☐ Upgrade: install version N-1, configure settings + a credential + start/interrupt
  a session (kill the host mid-recording), queue a delivery to an unreachable
  destination. Install version N: refused while recording unless `/FORCESTOP=yes`;
  afterwards settings/credential preserved, interrupted session recovered on first
  host start, pending delivery completes, one Programs entry, one PATH entry.
☐ Repair: re-run the same version; still works.
☐ Downgrade: refused without `/ALLOWDOWNGRADE=yes`.
☐ Uninstall: recordings/settings/queue remain by default; removed when the
  uninstall question is answered Yes.

## Real-service checks

☐ SharePoint: docs/graph-setup.md against a real tenant — both credential modes;
  interrupt a large upload (network off mid-transfer) and confirm resume from the
  correct offset; revoke permission and confirm the verbatim server error lands in
  manual-retry.

## Picture quality

Quality is a **user setting**, not a release gate: the preset (`archival`,
`sharp-text` default, `balanced`, `compact`), an optional numeric quantizer
override, and the frame rate are all adjustable at any time from the UI or
`captr settings set`. A reviewer who wants sharper small text raises the preset
or lowers the frame rate; nothing in the build needs to change. What the build
DOES guarantee is that the chosen settings are the ones actually used and
reported (encoder selection never silently substitutes, and the status view names
the encoder actually running).

## Endurance

☐ Ten-hour soak: `CAPTR_SOAK_MINUTES=600` with the `Soak` trait — no handle leak,
  no unbounded memory growth, wall-vs-encoded drift < 1 s, journal internally
  consistent, every segment verifies, coverage > 99.9 %. (Shorter runs of the same
  test — 3 and 4 minutes — pass on the development machine; only the clock differs.)
☐ GPU driver reset under load (e.g. vendor tool or `devcon restart` on the
  display adapter mid-recording): the session must gap honestly and continue or
  stop loudly — never hang or record silence.
