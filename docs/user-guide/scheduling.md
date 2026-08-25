# Scheduled recording

Captr has no built-in scheduler, on purpose. Windows already has a good one, and a
second one running in the background is exactly the kind of resident thing Captr
avoids. Scheduled recording is a Task Scheduler task that runs `captr`.

## ⚠ The one setting that will otherwise cost you a day

> **The task MUST be set to "Run only when user is logged on".**
>
> Choosing "Run whether user is logged on or not" puts the task in **session 0**,
> where the Windows screen-capture API cannot see your desktop. The recording will
> start. It will report success. It will produce files of exactly the right size.
> **Every frame will be black.**
>
> There is no error, because as far as the capture API is concerned session 0 *is*
> a desktop — it is just not yours. This is a Windows platform boundary. No setting
> in Captr can work around it, and no recorder can.

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
idle, `11` paused, `1` error, `2` bad usage, `3` no recorder reachable.

```bat
captr status
if %ERRORLEVEL%==10 echo Nothing is recording
if %ERRORLEVEL%==11 echo Recording is PAUSED - someone forgot
```

## Checking a schedule actually worked

After the first scheduled run:

```bat
captr recordings list
captr transfers list
```

The recording should be there with the duration you expected, marked finalised, and
your destinations should have received it. If the video is black, re-read the
warning at the top of this page.
