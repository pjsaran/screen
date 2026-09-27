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
| `Captr/{date:yyyy-MM}` | the same thing in a SharePoint library |

Missing folders are created. Separators you write are kept exactly as written; a
token can never introduce one of its own, so a pattern cannot accidentally create a
folder level you did not ask for or escape the destination folder.

The date used is **the recording's**, not today's. A transfer that fails at 23:59
and retries at 00:01 still lands in the folder it was queued for, and an old
recording sent again next week still files under the day it was made.

### What a destination folder may be

Around its tokens, a folder has to say exactly where it is:

- **A folder destination** must be a full path — `D:\Recordings\{date:yyyy-MM}` or
  `\\server\share\recordings` — not a relative one like `Recordings`, which would
  depend on whatever folder the recorder happened to start in.
- It may not contain `:` anywhere after the drive letter (that would write into a
  hidden part of a folder rather than a file you can see), and it may not start with
  a device prefix such as `\\?\` or `\\.\`.
- No folder may contain a `..` segment: it would put recordings outside the folder
  you named.
- **A SharePoint folder** is a path *inside* the document library, such as
  `Recordings/2026` — never a drive letter or a `\\server` path.

These rules arrived in a later version of Captr. A settings file saved before them
that breaks one **still records** — a folder rule never stops a recording — but no
change to the settings can be saved (in the window or with `captr settings set`)
until the folder is fixed, Diagnostics reports it, and a transfer to that
destination fails with the same words rather than writing somewhere you did not
expect.

## See the result as you type

The Settings page's naming pattern, and the destination editor's file-name and
folder boxes, show underneath what a recording made now would be called or where it
would go ("A recording started an hour ago would be named: …", "A recording made now
would go to: …"). When the pattern has a problem, the same line shows the problem
instead, in the words Save would use. That is the moment to notice that
`{date:dd-MM}` and `{date:MM-dd}` are not the same thing, rather than after the first
recording lands. Screen readers announce the preview as it changes.

## Bad patterns are caught when you save

Captr checks a pattern as you type (see above) and again the moment you save the
setting, not after an eight-hour recording has finished. It refuses:

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
underscores, trailing dots and spaces are trimmed, and every name Windows reserves
for a device gets a leading underscore rather than being rejected — `CON`, `PRN`,
`AUX`, `NUL`, `COM0` to `COM9`, `LPT0` to `LPT9` (and the superscript `COM¹`, `COM²`,
`COM³`, `LPT¹`, `LPT²`, `LPT³`), `CONIN$` and `CONOUT$`, with or without an extension
or a trailing space (`CON .mkv` is still the console to Windows). The same applies to
every folder level a pattern produces. A name longer than 200 characters is cut to
200 before the extension, never in the middle of a character.

Your recording is saved whatever your pattern says. If a name still cannot be used
when the recording finishes — a destination's pattern edited by hand into something
invalid, or a file another program is holding open — that one output falls back to
the default pattern, and then to the name it was recorded under. It is still kept
and still sent to every destination; only the nicer name is lost.
