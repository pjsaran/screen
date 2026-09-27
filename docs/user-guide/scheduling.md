# Scheduled recording

Captr has no built-in scheduler, on purpose. Windows already has a good one, and a
second one running in the background is exactly the kind of resident thing Captr
avoids. Scheduled recording is a Task Scheduler task that runs `captr`.

## ⚠ The one setting that will otherwise cost you a recording

> **The task MUST be set to "Run only when user is logged on".**
>
> Choosing "Run whether user is logged on or not" puts the task in **session 0**,
> where the Windows screen-capture API cannot see your desktop. Capture would start,
> the files would grow, and **every frame would be black** — Windows itself raises
> no error. This is a Windows platform boundary. No setting in Captr can work around
> it, and no recorder can.
>
> So Captr refuses. `captr start` in session 0 (or on any window station other than
> the signed-in user's) records nothing and exits `1` with a message naming the fix:
>
> ```
> Captr cannot record here: it is running in session 0 — a scheduled task set to
> "Run whether user is logged on or not", or a service — where there is no desktop
> to capture, so every frame would be black. Set the task to "Run only when user is
> logged on" (see Scheduling in the user guide).
> ```
>
> The task's **Last Run Result** shows `(0x1)`: the cue to change the setting, found
> after one run rather than as a black recording discovered afterwards. (Capture the
> command's output to a file if you want the message itself in a log.)

## The other settings that matter

In the task's properties:

| Tab | Setting | Set it to |
|---|---|---|
| General | Run only when user is logged on | **selected** (see above) |
| General | Run with highest privileges | **off** — recording needs no elevation |
| Settings | Stop the task if it runs longer than | **unticked** — otherwise it kills a long recording mid-flight |
| Conditions | Stop if the computer switches to battery power | **unticked** — same reason |
| Conditions | Start the task only if the computer is on AC power | **unticked**, unless you mean it |
| Settings | If the task is already running | "Do not start a new instance" |

Captr recovers from being killed, but you still lose the last few seconds. It is
cheaper not to be killed.

"Do not start a new instance" is safe because `captr` never waits for ever: every
request to the recorder has a time limit (two minutes; ten for `start` and `stop`;
an hour for `recover`), after which the command exits `3` saying what to do. A stuck
recorder therefore costs one run, not every run after it. See
[time limits](command-line.md#time-limits).

## Worked example: weekdays, 09:00 to 17:30

Start:

```bat
schtasks /Create /TN "Captr start" ^
  /TR "captr.exe start --label scheduled" ^
  /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 09:00 /IT
```

Stop:

```bat
schtasks /Create /TN "Captr stop" ^
  /TR "captr.exe stop" ^
  /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 17:30 /IT
```

`/IT` is the command-line form of "run only when user is logged on".

The installer put `captr.exe` on your PATH; use the full path
(`"C:\Program Files\Captr\captr.exe"`) if you prefer to be explicit, which is
usually the better choice in a scheduled task.

`--label scheduled` makes `{label}` available in your
[naming pattern](naming-patterns.md), so scheduled recordings are recognisable at a
glance.

## Double-firing is safe

Schedulers fire twice more often than anyone expects — a wake from sleep, a missed
run catching up, an overlapping trigger. Two behaviours exist specifically for this:

- `captr start` while already recording reports the existing session and exits `0`.
- `captr stop` while idle reports idle and exits `0`.

Neither can harm a recording, and neither looks like a failure to the scheduler.

## Branching on the result

Scripts can rely on the [exit codes](command-line.md#exit-codes): `0` success, `10`
idle, `11` paused, `1` error (including a start refused in session 0), `2` bad
usage, `3` no recorder reachable or no answer in time.

```bat
captr status
if %ERRORLEVEL%==10 echo Nothing is recording
if %ERRORLEVEL%==11 echo Recording is PAUSED - someone forgot
```

If the recording stopped by itself before the stop task ran — the disk nearly full,
say — `captr status` still exits `10`, and its message says so: "The last recording
stopped on its own: …", with the reason. `captr status --json` carries the same in
`lastOutcome`, while the recorder is still running.

## Checking a schedule actually worked

After the first scheduled run:

```bat
captr recordings list
captr transfers list
```

The recording should be there with the duration you expected, marked finalised, and
your destinations should have received it. If there is no recording at all, look at
the task's Last Run Result: `(0x1)` usually means the task is set to run whether the
user is logged on or not — see the warning at the top of this page.
