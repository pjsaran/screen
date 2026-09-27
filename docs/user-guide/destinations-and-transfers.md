# Destinations and transfers

A **destination** is a place finished recordings are copied to. A **transfer** is
one copy on its way to one destination.

Having no destinations is a perfectly good choice — recordings simply stay in the
working folder. Everything on this page is optional.

## Adding a destination

**Settings → Destinations → Add destination.**

Two kinds work today:

- **Local or network folder** — a folder on this PC, a mapped drive, or a UNC
  share like `\\server\share\recordings`. The folder must be a full path; see
  [what a destination folder may be](naming-patterns.md#what-a-destination-folder-may-be).
- **SharePoint** — a document library, uploaded through Microsoft Graph. Needs a
  one-time [app registration](sharepoint-setup.md).

Four more (Amazon S3, Azure Blob Storage, Google Drive, SFTP) are listed and marked
"coming soon". They are shown rather than hidden because people ask whether Captr
can upload to S3, and the honest answer belongs where they look for it. Choosing
one explains that it is not built and refuses to save.

Each destination has:

| Field | |
|---|---|
| **Name** | Your label. It is how the transfer is identified on the Transfers page, so make it recognisable. |
| **Folder** | Where the files land. Accepts [naming tokens](naming-patterns.md), so `D:\Recordings\{date:yyyy-MM}` gives you a folder per month. Missing folders are created. |
| **File name at this destination** | Optional. Leave it empty to keep the recording's own name; fill it in when an archive wants a different convention from your working copy. |
| **Enabled** | Unticked destinations are skipped. Useful for turning one off without losing its configuration. |

Saving the destination saves it — there is no second Save to remember. While you
type, the editor shows where a recording made now would land and what it would be
called, or what is wrong with the pattern.

For SharePoint, **Test connection** signs in with the secret you have just typed, so
a pass proves that secret works, not one remembered from earlier.

## How a transfer works

When a recording finishes, one transfer is queued **per enabled destination**. Each
is independent: if a recording is going to two places and one fails, the other is
unaffected, and retrying the failed one cannot disturb the successful one.

For a folder destination, Captr:

1. checks there is enough free space — measured for the folder itself, so a UNC
   share or a volume mounted into a folder is measured correctly;
2. copies to a temporary `.partial` file with a name of its own, so an interrupted
   copy never leaves something that looks finished and never overwrites a file
   already there, even one with the same temporary name;
3. flushes the copy to disk, reads it back, and compares its size and SHA-256
   against the source — this is what catches a network filesystem that lied about
   writing;
4. renames it into place, adding ` (2)` if something is already there. **Captr
   never overwrites a file at a destination.** A copy that fails is deleted, never
   left half-written.

For SharePoint it uses a resumable upload session, recording the confirmed position
after every chunk, so a dropped connection resumes rather than starting again. An
upload session that SharePoint has expired in the meantime is replaced by a new one
— after first checking whether the file already arrived, so a lost final reply
never uploads a second copy. Uploads go only to `https` addresses. A finished upload
is checked by its size.

The queue survives reboots. A transfer interrupted by a shutdown carries on when
Captr next runs.

**A failed transfer can never harm the local recording.** The worst thing that can
happen is a line on the Transfers page waiting for you — and when a transfer needs
you, Home says so too ("1 transfer needs attention — see Transfers"), so you do not
have to open the Transfers page to find out.

## The Transfers page

One line per transfer: the file, the destination, its size, when it was queued, and
its state.

| State | Meaning | What you can do |
|---|---|---|
| **QUEUED** | Waiting its turn. | Stop it. |
| **SENDING** | Being copied now, with a progress bar. | Stop it. |
| **RETRYING** | Failed, waiting out a backoff. The line says until when. | Stop it. |
| **TRANSFERRED** | At the destination and verified. | Nothing. |
| **GAVE UP** | Out of automatic attempts. | Fix the cause, then Retry. |
| **REFUSED** | The destination said no — permission, quota, or policy. The server's exact words are shown underneath. | Fix the cause, then Retry. |
| **SIGN-IN NEEDED** | The stored credential stopped working. | Enter the client secret again in Settings and save — that puts these back in the queue by itself. |
| **STOPPED** | You stopped it. | Retry when you are ready. |

**Retry** appears only on transfers that have stopped. **Stop** appears only on
transfers that are still going. Neither appears on a completed transfer — a button
that does nothing is worse than no button.

**Retry all failed** re-queues everything needing attention in one click, for after
you have fixed whatever was wrong.

If Retry or Stop fails, the error stays in a banner at the top of the page until an
action succeeds. The list refreshes every few seconds without rebuilding itself, so
keyboard focus and a screen reader's place stay where they were. If Captr's transfer
database cannot be opened, the page says so in words instead of the window failing
to open.

### When SharePoint says no

| What happened | State | What to do |
|---|---|---|
| Microsoft sign-in refused the client secret (mistyped, expired, or deleted in Entra) | **SIGN-IN NEEDED** | Enter the current secret in the destination and save; the transfer goes back in the queue by itself. Nothing was sent. |
| SharePoint rejected the access token | **SIGN-IN NEEDED** | The same. |
| The app has no permission on that site (with `Sites.Selected`, the site was never granted) | **REFUSED**, with SharePoint's own reason underneath | Ask the SharePoint administrator to grant the app access to the site ([SharePoint setup](sharepoint-setup.md)), then Retry. Every automatic retry would be refused the same way, so none is made. |

In every case the local recording is untouched.

### How far back the list goes
The page shows **finished transfers from the last 30 days**. Anything still going —
queued, sending, waiting to retry, or failed — is shown however old it is, because a
transfer that has been stuck for two months is exactly the one you need to see.

Older finished records are removed from Captr's database once the recording they
describe is no longer on this machine, which keeps the file small. Nothing on a
destination is ever touched.

## Retries

**Settings → Transfers.**

| Setting | Default | |
|---|---|---|
| Automatic attempts | 5 | How many times a failing transfer is retried before it stops and waits for you. |
| First retry after | 30 seconds | The first wait. Each later wait is roughly twice the one before. |
| Longest wait | 1800 seconds (30 min) | The ceiling on that doubling. |

The waits are jittered by ±20% so several transfers to the same dead destination do
not all retry in lockstep.

Running out of attempts loses nothing. The transfer parks with its last error and a
Retry button; the local recording is untouched. If the destination is simply down
for the weekend, press Retry on Monday.

For SharePoint, an answer that means "try again later" — a timeout (HTTP 408), a
locked file (423), throttling (429), or a server error (5xx) — counts as a
temporary failure, and when SharePoint says how long to wait (`Retry-After`, as
seconds or as a time), that wait is honoured. An error Captr did not expect is still classified
rather than stopping the queue: a credential problem waits for sign-in, access
denied or a bad path waits for a person, and anything else is retried within the
attempt limit.

The Transfers page states the policy in words at the top, so "attempt 3 of 5" always
means something.

### Stopping a transfer

**Stop** halts it — including one that is mid-copy, within a couple of seconds. It
stays in the list with everything it had, and starts again only when you press
Retry. Nothing local is deleted, and anything already at the destination is left
alone.

Use it when you know a destination is down and the automatic retries are just noise.

## Sending an old recording somewhere

**Recordings → … → Send to destinations again.**

Useful after adding a destination, fixing one, or when a transfer was abandoned.

It is offered only when it would actually do something: at least one enabled
destination must be missing this recording. When every destination already has it,
the menu item is greyed out and says so — pressing it would only produce a second
copy called "name (2)".

It never queues a duplicate. For each enabled destination:

- already transferred — skipped;
- already on its way (queued or sending) — left alone, so pressing it twice sends
  one copy, not two;
- stopped, given up, refused, or waiting for sign-in — **that** transfer is put back
  in the queue, rather than a second one added beside it;
- never sent — a new transfer is queued.

Only the recording's own finished files are sent — names that point outside the
recording's folder are ignored (and reported by `captr recordings verify` as
tampering).

## Retention and transfers

Captr deletes a recording's folder by itself only after the retention period, and
only once it has been transferred and **every one of that recording's own transfers
has completed** — see [Retention](recording.md#retention). A transfer that is stuck,
stopped, or waiting for sign-in keeps the recording on this PC, so bringing it back
with Retry or Send again is what eventually lets retention reclaim the space.
Recordings are judged by their own transfers only: a folder whose name happens to
begin with another's (`S1` and `S10`) is never deleted on the strength of the other
one's transfers.

The command-line equivalent is `captr recordings resend <folder>`.

## Timeouts

Every attempt has a deadline that scales with the file: five minutes minimum, and
longer for larger files (about seventeen minutes for a gigabyte). A share that
stops responding mid-copy is abandoned and retried rather than sitting on "Sending"
forever.
