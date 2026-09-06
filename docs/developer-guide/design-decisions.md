# Decisions and deviations

Where Captr departs from the letter of `SPEC.md`, or resolves something the spec
left to judgement, the reason is recorded here. SPEC §0 asks for exactly this:
"if any requirement here conflicts with something you would normally do, follow
this file and flag the conflict."

## Deviations from the spec's letter

**The Graph SDK is not used; the upload protocol is implemented directly.**
SPEC §3's table lists `Microsoft.Graph`. Its `LargeFileUploadTask` hides the
per-chunk confirmed offset that SPEC §7 requires us to persist after *every*
chunk, and it resists pointing at a local mock server for the interrupted-resume
test. The resumable-upload protocol is three HTTP calls; implementing it directly
gives both the persistence and the test. Licensing is unaffected (MIT either way),
and one fewer dependency passes the licence gate.

**The UI polls status at 1 Hz rather than receiving pushed events.**
SPEC §4 describes the host pushing state. A poll over the same pipe produces
identical UI behaviour with fewer moving parts, and it makes the UI stateless by
construction — which is what §4 actually cares about ("the UI is a view… can be
closed, killed, or relaunched mid-recording"). Reattaching after a UI restart is
just the next poll.

**Global hotkeys are two toggles, not three separate keys.** SPEC §8/§9 list
start, pause, and stop hotkeys. A hotkey is pressed without looking at the screen,
and separate keys make "stop while idle" and "start while already recording" both
possible and both useless. One key that starts when idle and stops when recording,
and one that pauses when recording and resumes when paused, is strictly the same
set of actions with the failure modes removed.

**Destination kinds Captr cannot transfer to are still listed in the picker.**
SPEC §7 defines two destinations, local/UNC folders and SharePoint. The settings UI
also lists S3, Azure Blob, Google Drive, and SFTP, each marked "coming soon",
disabled for saving, and rejected by `SettingsValidator` if one reaches settings by
another route. People ask "can it upload to S3?", and the honest answer belongs
where they look for it rather than in an absence.

**Settings live in LOCAL application data, not roaming.** SPEC §8 says roaming.
Almost everything in the file is bound to this machine — display selections are EDID
identities of physical monitors, the working folder and folder destinations are
absolute paths, and the credential a SharePoint destination names is DPAPI-bound to
this user *and* machine, so it cannot follow the file anyway. Keeping every piece of
Captr's state in one folder also makes a backup, a support bundle, or a clean-up a
single place rather than two. Moving a configuration between machines is what
`settings export` / `import` is for, where the user chooses what applies. An older
installation's roaming file is moved across on first load, so upgrading loses nothing.

**The application collects the SharePoint secret itself.** SPEC §7 requires that no
secret is ever accepted as a command-line argument and that secrets live in Windows
Credential Manager — both still hold. What changed is who does the typing: the
destination dialog takes the client secret directly and stores it, deriving the
credential entry name from the destination's name, instead of asking the user to
invent a name, run `captr auth set-secret`, and type that same name back in. The
secret still never reaches settings.json, is never read back into the UI, and
`captr auth set-secret` remains for scripted setup.

**Global hotkeys ship with defaults.** Ctrl+Alt+F9 toggles recording and Ctrl+Alt+F10
toggles pause. A hotkey nobody configured is a feature nobody discovers, and a clash
is reported by name at startup rather than failing silently.

**"Delivery" is called "transfer" everywhere.** SPEC §7 calls this "delivery". The
word is used in the trade for the thing a courier does to a parcel, which is a poor
fit for "copy this file to a folder and verify it landed" — and "the delivery queue"
consistently read to people as something about the recording rather than about a
copy of it. The rename is complete rather than cosmetic: the namespace, the classes,
the SQLite table and its file, the IPC verbs, the CLI verb, and the UI page. An
existing `delivery.db` is moved to `transfers.db` and its table renamed on first
open, so an upgrade keeps its pending transfers.

**Automatic retries are bounded by a configurable count, and running out parks the
transfer for a person.** SPEC §7 asks for "exponential backoff with jitter,
bounded". The bound is now an explicit, user-visible number rather than an implicit
ceiling on the delay, because a transfer retrying invisibly forever is
indistinguishable from a broken queue. Reaching the limit loses nothing: the row
keeps its last error, the local recording is untouched, and Retry starts a fresh
run. The Transfers page states the policy in words so "attempt 3 of 5" means
something.

**A transfer can be stopped by hand.** Not in the spec. A destination that is known
to be down produces retries that are pure noise, and the only alternative was
deleting the destination. Stop parks the row — including one mid-copy, which is
noticed within two seconds because the worker re-reads its own row — and Retry is
the only thing that starts it again. Nothing local is touched either way.

**Destination folders accept the same naming tokens as file names.** SPEC §7 defines
tokens for the delivered file's name only. Filing recordings into dated or
per-machine folders is the other half of the same job, and doing it by hand defeats
the point of automatic transfer. The expanded folder is resolved when the transfer
is QUEUED and stored on the row, so a transfer that fails at 23:59 and retries at
00:01 still lands where it was meant to, and a recording sent again next week still
files under the day it was made.

**The tray icon does not animate.** SPEC §9 lists the recording state as
"paused-animated" alongside idle, recording, and error. A blinking icon reads as an
alert rather than as a status, and having one in peripheral vision for an
eight-hour recording is tiring. The four icons are instead the same rounded display
with a different screen colour AND a different cut-out glyph — colour alone never
carries the meaning, since recording and error are both red. §9's actual requirement
("unmistakable at a glance") is met better standing still, and nothing runs a timer
for the length of a recording.

**The status page is called Home.** It is the page the application opens on and the
one people return to, and "Status" described the top card rather than the page.

**Diagnostics runs health checks, not just an export button.** SPEC §9 asks for a
support bundle. That answers "send this to someone who can help", which is the
second question; the first is "what is wrong?". `HealthReport` answers it on the
page — settings, FFmpeg, displays, the proven encoder, disk measured in HOURS of
recording, a SharePoint secret that has gone missing from Credential Manager, and
anything stuck in the queue — with a fix beside anything that is not right. It is
read-only by construction: no host is started, no FFmpeg is launched, no network
call is made, because a diagnostic must never alter what it is diagnosing.

**Adding, editing, or removing a destination saves immediately.** The destination
dialog has its own Save button, so requiring a second Save on the page underneath
was a trap — and one with teeth, because the dialog stores the SharePoint secret in
Credential Manager as soon as it closes. Leaving the page without the second Save
left a stored secret that settings.json did not point at, which looks exactly like
"changing the secret did nothing".

**Buttons are uppercase and letter-spaced.** They read as one voice with the
navigation rail, the column headers, and the status chips. It is applied by a single
keyed style whose `ContentTemplate` does the transformation, so the labels stay
written in ordinary sentence case in the XAML — `Retry all failed` is greppable and
appears in tooltips and documentation as itself. The style is deliberately KEYED
rather than implicit for `ui:Button`: WPF-UI builds its own controls out of that type
(a NumberBox's up/down chevrons are two of them) and an implicit style would replace
their icon content with a tracked string.

Note what this cannot cover: message boxes, the folder picker, and the title bar are
Windows's own and keep sentence case whatever Captr does.

**The installer says what it is about to do to an existing installation.** SPEC §11
requires only that a downgrade be refused. Reinstall, upgrade, and downgrade are
three different events and were previously indistinguishable — the installer simply
proceeded. Each is now named, with its consequences, and silent installs are
unaffected because they take the default answer.

**A first install creates settings.json.** By calling `captr settings init`, not by
writing JSON from the installer script: the defaults, the schema version, and the
validation then all come from the product and the script cannot drift from them. It
runs as the ORIGINAL user rather than the elevated one, because a per-machine install
runs elevated and settings belong to the person who ran setup.

**The uninstaller asks about data with two labelled choices, after confirming the
uninstall.** It used to be a Yes/No box — "Also delete recordings, settings, and the
delivery queue?" — asked BEFORE Windows had even confirmed the uninstall. Answering
that wrongly in a hurry destroys footage. Now the uninstall is confirmed first, each
choice states what it does, keeping is the default, and deleting is confirmed twice.

**Screen capture falls back to GDI when Desktop Duplication cannot start.** SPEC §5
names `ddagrab` (the Desktop Duplication API) as the capture path. On virtual
desktops — AWS WorkSpaces was the machine that surfaced it — the display driver
cannot create the D3D11 device ddagrab needs, so every encoder trial died in
capture and the machine reported "no working encoder" with all encoders fine.
Encoder selection now proves capture the same way it proves encoders: ddagrab
first, and if capture itself is what failed, the same candidates again with
`gdigrab` inputs. The winning combination is cached, the plan records it, and
Diagnostics says when the compatibility path is in use and why. GDI costs real
CPU, which is why it is a fallback and never the first choice.

**SharePoint uploads are addressed by an explicit Drive ID.** The uploader
previously posted to `drives/default/…`, and Graph has no such alias — every
upload failed with "invalid drive id". The destination now stores the document
library's drive id, typed by the user and verified by the editor's Test
connection button. Resolving the id from the site URL at transfer time was
rejected: it needs an extra Graph permission, adds a round-trip to every upload,
and a site can hold several libraries — the id says exactly which one. A
destination saved by an older version lacks the id; that is reported by
Diagnostics and fails the transfer with the fix named, but is deliberately NOT a
settings-validation error, because validation blocks *recording* and a
transfer-side gap must never do that.

**The bundled FFmpeg is the GPL build, and libx264 leads the software tier.**
SPEC §2 mandates an LGPL build to keep GPL out of the installer. The owner's
deployment is internal-only — nothing is conveyed outside the organisation, so
the GPL's distribution obligations never activate — and the owner explicitly
accepted the trade (2026-08-24) to gain libx264: unlike openh264, it has a true
CRF mode, so the Quality setting behaves identically on CPU-only machines (AWS
WorkSpaces) and GPU machines, and typically produces smaller files for static
screen content than a fixed bitrate would. Guard rails: `ffmpeg.lock.json` now
records the intended `licenceFlavor` and `fetch-ffmpeg.ps1` asserts the binary
matches it in BOTH directions; the licence report labels the build honestly; the
source-offer doc states the exit path. Reverting for external distribution is a
lock-file edit — the catalog reads what the shipped binary contains, so the
software tier degrades to openh264 with no code change. libx265 stays excluded
on CPU-cost grounds, not licensing.

## Judgement calls the spec left open

**"Re-adopted when the host restarts" means the new host takes ownership and
finalises, not resumes.** §4 says a surviving encoder is re-adopted; §6 says every
host start finalises unfinalised sessions. Captr does both in that order:
identify the orphan (PID + start time + image path + the in-file session marker),
stop it, then finalise — preserving every frame that reached disk. It does not
resume supervising the old session, because a new host cannot reconstruct the
original disk guard, tracker, and encoder plan without replaying state the journal
was never meant to carry. Footage is preserved either way; this way the
recording's boundaries stay honest.

**Retention never deletes a recording that exists nowhere else.** §7 permits
deletion once "every enabled destination has confirmed" — which is vacuously true
when there are no destinations. Deleting then would destroy the only copy, so
`RetentionCleaner` additionally requires at least one *completed* transfer. Design
priority #1 outranks reclaiming disk. Local-only recordings are deleted by the
user, with the typed confirmation.

**A host crash costs the encoder's unflushed output buffer, not the segment.**
§13 rule 1 allows losing "at most the segment in progress". In practice FFmpeg
buffers file output through 512 KB that no documented option removes
(`-flush_packets`, `-avioflags direct`, `-blocksize` were all measured and
changed nothing), so a killed encoder loses only that tail — seconds, less at
higher bitrates. Capping Matroska clusters at 2 s pushed ~50 % more bytes to disk
before a kill, and is now the default.

**Picture quality is a user setting, not a build-time calibration.** The spec asks
for a default that keeps small text legible and for verification against real
content. Instead of one bundled "quality preset", Captr exposes the three
independent controls that actually determine the result — frame rate, speed preset,
and quality level — all adjustable at any time from the UI or CLI. Bundling them
made "cheap CPU but a good picture" impossible to ask for, which is exactly what a
laptop recording a terminal wants. What the build guarantees instead is that the
chosen settings are the ones actually used and honestly reported — encoder
selection never silently substitutes, and status names the encoder actually running.

**Starting a recording must feel instant, so the encoder cache carries the measured
rate.** Proving an encoder and measuring its byte rate were two trial encodes on
every single start, which put five to ten seconds between pressing Record and
recording. They are now one trial whose result — winner AND rate — is cached against
the GPU, driver, canvas, and encoding settings, so only the first recording on a
machine pays for it. The spec's instruction to confirm a cached winner before
trusting it is honoured differently: the recording itself is the confirmation, and a
session that dies within 30 seconds clears the cache so the next start re-probes.
That trades one failed start, in a case that requires a driver to break without
changing its version, for ten seconds off every normal start.

**The tray icon blinks while recording; a static icon was tried and rejected.** The
first build had the icon simply change colour, on the reasoning that a blinking icon
is the kind of thing that irritates people. In use it was wrong: the icon is 16 px,
Windows 11 hides it in the notification-area overflow by default, and a still red
dot reads as "installed", not as "recording right now". Motion is the only property
of a tray icon that survives being small and half-hidden. `TrayPresenter` alternates
a bright and a dim icon every 800 ms while recording, and deliberately holds the
BRIGHT icon while stopping and finalising, so "still busy" is never mistaken for
"still recording".

**Display size comes from the display mode, not from DXGI's desktop rectangle.**
`DXGI_OUTPUT_DESC.DesktopCoordinates` is in virtual-desktop coordinates, and Windows
SCALES those for a process that is not per-monitor DPI aware. On a 1920x1080 screen at
125%, a DPI-aware process reads 1920x1080 and an unaware one reads 1536x864 — same
monitor, same moment. Desktop Duplication always captures the real 1920x1080, so
planning from the scaled number builds a canvas SMALLER than the frames and ffmpeg
rejects the whole graph with "Padded dimensions cannot be smaller than input
dimensions". `EnumDisplaySettings` reports the mode the hardware is actually in and is
not virtualised, so every process gets the same answer.

This was found the slow way: every encoder candidate, hardware and software alike,
failed its trial with a bare "Invalid argument", which reads like broken drivers. The
real message was on the FIRST line of ffmpeg's output and the trial kept only the last
300 characters. `EncoderTrial.Tail` now keeps both ends — a diagnostic that discards
the start of an error message is worse than no diagnostic.

**The transfer queue has a 30-day history window, and pruning follows retention, never
leads it.** One row per (recording, destination), never removed, meant the Transfers
page eventually listed thousands of "completed" rows for recordings long gone from the
machine. `ListRecent` shows finished transfers from the last 30 days — anything
UNFINISHED is listed however old, because a transfer stuck for two months is exactly
the one somebody needs to see — and `Prune` deletes old finished rows, then VACUUMs,
since SQLite does not return freed pages on its own.

`Prune` deletes a row only when its recording is also gone from disk. That is not
caution for its own sake: `RetentionCleaner` decides whether a session folder may be
deleted by asking the queue whether every transfer for it completed, so discarding
those rows while the folder still exists would make it conclude the recording had
never been transferred anywhere and keep it for ever. History outlives the file it
describes, never the other way round.

**A lost desktop is classified by FFmpeg's real wording, and that wording is tested
against the shipped binary.** Supervision splits an unexpected encoder exit into a
fault — which counts toward the one permitted fallback and eventually stops the
session loudly — and a capture-access loss, which retries forever with backoff and
counts toward nothing, because no encoder on earth can record a desktop that is not
there. The split is decided by matching literal strings in FFmpeg's log.

Those strings were originally written from memory: `ACCESS_LOST`, `ACCESS_DENIED`,
and `"Failed to duplicate output"`. FFmpeg prints none of them. The classifier
therefore never once recognised a lost desktop in production, and the unit tests
agreed with it, because they fed it the same three invented lines. Every locked
workstation, UAC prompt, and disconnected remote session was booked as an encoder
fault instead.

On a desktop PC this was survivable: hardware encoders exist, so the fallback rung
absorbed the first three faults. On an AWS WorkSpace it was not. There is no
hardware encoder there, so the plan starts on the software encoder and
`FallbackArguments` is null — meaning the very first time the fault counter reached
three, the supervisor skipped the fallback branch and went straight to stopping the
session. Three screen locks inside five minutes ended the recording, and the user
saw it simply stop.

The fix is three-part, because one bug had three enablers. The signatures are now
verbatim strings pulled out of `tools/ffmpeg/bin/ffmpeg.exe` and cover gdigrab as
well as ddagrab — WorkSpaces capture through GDI, whose failures look nothing like
DXGI's. `CaptureLossSignatureTests` reads the shipped binary and fails if any
signature is not really in it, which is the only kind of test that can hold a
contract with someone else's log text. And classification now reads only the log
lines the *current* encoder process wrote: the ring buffer spans the whole session,
so a single stale `Error` line used to condemn every later exit, including clean
ones.

Two smaller faults in the same path were fixed alongside, both of which made the
first one worse. The monitor loop fed the policy `LatestProgress` even when it still
held the dead process's final snapshot — which set the stall clock to a moment
already in the past and killed the fresh encoder instantly (guaranteed after any
capture-loss backoff, since the backoff itself makes the old progress stale), and
which reset the retry backoff before a single new frame existed, pinning it at one
second forever. And the WTS session-change mapping had remote connect and disconnect
the wrong way round, so a WorkSpaces journal announced that the desktop had returned
at the exact moment it went away.

**Captr reports what will interrupt an unattended recording; it never overrides it.**
A recording left running alone is at the mercy of four machine behaviours, and they
are not one problem. System sleep and display power-off are *ours to prevent* and
mandatory to prevent: `ExecutionStateHolder` holds a system-and-display-required
execution state for the whole session, because a powered-off display makes Desktop
Duplication return black frames — a recording that looks healthy and contains
nothing.

The screen saver and the workstation lock are a different mechanism and not ours to
touch. Both run off the input-idle timer — time since a real keystroke or mouse move
— which no execution-state request affects. There is no Windows API that suppresses
the Group Policy machine inactivity lock at all; the only thing that defeats it is
synthesising fake input on a timer, which is a mouse jiggler by another name. That
overrides a security control the machine's owner deliberately set, and on a managed
machine it is the sort of thing endpoint tooling flags. Recording someone's screen is
already a capability that has to be beyond reproach; quietly defeating their lock
policy to do it is not a trade Captr makes.

So `IdleLockPolicy` reads the machine's real configuration — policy keys first, since
on a managed machine the user's own Personalisation page may say something the policy
overrides — and the answer is surfaced twice: as the **Unattended recording**
diagnostic, and as a note journaled at recording start, from the same `Describe()`
so the two can never disagree. Timing is the point. Told at start, it is a setting to
change; discovered at the end, it is three lost hours.

The two outcomes are reported separately because they are not equally bad. A locking
screen saver or an inactivity lock switches to the secure desktop, capture access is
lost, and the gap is honest and self-healing. A **non-locking** screen saver stays on
the ordinary desktop, so capture works perfectly and faithfully records the screen
saver: no gap, no warning, content silently gone. Collapsing them into one warning
would hide the one that lies.

## Things deliberately not built

- **No scheduler, service, resident agent, telemetry, or auto-update** (§1, §11).
  Scheduled recording is Windows Task Scheduler invoking the CLI, and the host
  exits when idle.
- **No clip extraction at all.** §9 offers extracting a portion of a recording by
  stream copy at segment boundaries. It was built, then removed: cutting only on
  five-minute boundaries is too coarse to be the clip people actually want, and
  anything finer requires re-encoding, which softens the picture and contradicts the
  point of a stream copy. Every video player and editor already trims, and Captr's
  job is producing footage that survives, not editing it.
- **No second fallback rung, and never a capture-method change MID-SESSION.**
  Frame rate may degrade indefinitely; capture may not. Once a recording is running
  it keeps the capture method it started with, so the footage cannot silently change
  character halfway through (§1, §6). The capture method is chosen once, at start,
  by proving it — see "Screen capture falls back to GDI when Desktop Duplication
  cannot start" above.
