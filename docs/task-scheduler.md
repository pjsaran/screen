# Scheduled recording with Windows Task Scheduler

Captr deliberately has **no built-in scheduler** (SPEC §1). Scheduled recording is a
Task Scheduler task that runs the `captr` command line. Anything the UI can do, the
command line can do.

## ⚠ The one setting that will otherwise cost you a day

> **The task MUST be configured to "Run only when user is logged on".**
>
> Selecting "Run whether user is logged on or not" places the task in **session 0**,
> where DXGI Desktop Duplication cannot capture the desktop. The recording will
> start, appear to succeed, produce segment files — and every frame will be black.
> There is no error, because to the capture API session 0 IS a desktop; it is just
> not yours. This is a Windows platform boundary, not a Captr bug, and no setting
> in Captr can work around it.

Also configure, on real long-recording tasks:

- **General → Run only when user is logged on** (see above).
- **General → Run with highest privileges**: leave OFF unless something in your
  environment genuinely requires it; recording needs no elevation.
- **Settings → Stop the task if it runs longer than**: **disable** — it would kill
  a long recording mid-flight (Captr recovers, but you lose up to a segment).
- **Conditions → Stop if the computer switches to battery power**: **disable** for
  the same reason.
- **Settings → If the task is already running**: "Do not start a new instance" —
  though `captr start` while recording is a harmless success by design.

## Worked example: record 09:00–17:30 on weekdays

Create the start task (run once from an elevated prompt, or use the GUI):

```bat
schtasks /Create /TN "Captr start" ^
  /TR "captr.exe start --label scheduled" ^
  /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 09:00 /IT
```

And the matching stop task:

```bat
schtasks /Create /TN "Captr stop" ^
  /TR "captr.exe stop" ^
  /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 17:30 /IT
```

`/IT` is the flag form of "run only when user is logged on" (interactive token).
The installer put `captr.exe` on `PATH`; use the full install path if you prefer.

## Branching on exit codes

Schedulers and wrapper scripts can rely on the documented exit codes
(see `docs/cli.md`): `0` success, `10` idle, `11` paused, `1` error, `2` bad
usage, `3` no host reachable. Two behaviours exist specifically for schedulers
(SPEC §10):

- `captr start` when already recording: reports the existing session, exits `0`.
- `captr stop` when idle: reports idle, exits `0`.

Schedulers fire twice more often than anyone expects; neither double-fire can harm
a recording.

## Verifying your schedule

After the first scheduled run, check:

```bat
captr recordings list
```

The session should appear with the expected duration, `finalised`, and your
configured destinations should have received the named file (`captr delivery list`).
