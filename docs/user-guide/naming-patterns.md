# Naming patterns

A naming pattern is ordinary text with `{tokens}` in it. Captr replaces each token
when a recording finishes.

Patterns are used in three places, all with the same tokens:

| Where | Setting |
|---|---|
| The finished recording's file name | Settings → Files → Naming pattern |
| The name a copy takes at one destination | that destination's "File name at this destination" |
| The **folder** a copy lands in | that destination's Folder |

The default is `{machine} {date} {start}` — for example
`WORKSTATION-4 2026-08-21 14-32-07.mkv`.

## The tokens

| Token | Becomes | Accepts a format? |
|---|---|---|
| `{date}` | `2026-08-21` | yes |
| `{start}` | `14-32-07` | yes |
| `{end}` | `16-47-51` | yes |
| `{duration}` | `2h15m`, `12m30s`, `45s` | no |
| `{machine}` | the computer's name | no |
| `{user}` | the account that recorded | no |
| `{label}` | the label passed to `captr start --label`, or nothing | no |

Times are in the timezone the recording was made in, not UTC. File names are for
people.

## Choosing your own separators

`{date}`, `{start}`, and `{end}` accept a .NET date/time format after a colon, so
the separators are yours:

| Pattern | Produces |
|---|---|
| `{date}` | `2026-08-21` |
| `{date:yyyy_MM_dd}` | `2026_08_21` |
| `{date:yyyyMMdd}` | `20260821` |
| `{date:dd MMM yyyy}` | `21 Aug 2026` |
| `{start:HH_mm_ss}` | `14_32_07` |
| `{start:HHmm}` | `1432` |

Omit the format and you get the default from the table above.



### `HH` for the 24-hour clock, `hh` for the 12-hour one
This is the ordinary .NET rule and it is easy to get wrong, so it is worth stating:
a lowercase `h` is the **12-hour** hour, a capital `H` is the **24-hour** hour.

`{start:hh_mm}` names a 6 pm recording `06_05` — the same as one made at 6 am. If
you want an evening recording to say `18_05`, use `{start:HH_mm}`. If you prefer the
12-hour clock, add `tt` for the am/pm: `{start:hh_mm_tt}` gives `06_05_PM`.

Captr does not choose for you; both are valid.

## Dated folders

The same tokens work in a destination's **Folder**, which is how you get recordings
filed automatically:

| Folder | Result |
|---|---|
| `D:\Recordings\{date:yyyy-MM}` | `D:\Recordings\2026-08\` — one folder per month |
| `\\nas\video\{date:yyyy}\{date:MM-MMM}` | `\\nas\video\2026\08-Aug\` |
| `\\nas\video\{machine}` | one folder per PC |
| `Shared Documents/Captr/{date:yyyy-MM}` | the same thing in a SharePoint library |

Missing folders are created. Separators you write are kept exactly as written; a
token can never introduce one of its own, so a pattern cannot accidentally create a
folder level you did not ask for or escape the destination folder.

The date used is **the recording's**, not today's. A transfer that fails at 23:59
and retries at 00:01 still lands in the folder it was queued for, and an old
recording sent again next week still files under the day it was made.

## Bad patterns are caught when you save

Captr checks a pattern the moment you save the setting, not after an eight-hour
recording has finished. It refuses:

- an unknown token — `{whoops}` — and lists the ones that exist;
- a format on a token that does not take one;
- a format that is not a valid date/time format;
- a format that would produce a character Windows forbids.

The last one has an obvious trap: `{start:HH:mm:ss}` looks right, but a colon
cannot appear in a Windows file name. Captr says so, shows what the format would
have produced, and suggests `-` or `_` instead.

## Names that already exist

Captr never overwrites a recording, locally or at a destination. If the name is
taken, it adds ` (2)`, ` (3)`, and so on before the extension.

Names are also made safe automatically: characters Windows forbids become
underscores, trailing dots and spaces are trimmed, and the reserved device names
(`CON`, `PRN`, `AUX`, `NUL`, `COM1`…, `LPT1`…) are prefixed rather than rejected —
your recording is saved whatever your pattern says.
