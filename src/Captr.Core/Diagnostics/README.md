# Diagnostics

Answers two questions that are asked in two very different moods:

- **"Is this installation going to work?"** — asked before something goes wrong, or
  as the first step when it has. `HealthReport`.
- **"Here is everything you need to work out what happened."** — asked when the
  answer is not obvious and somebody else has to look. `SupportBundle`.

## HealthReport

One read-only pass over settings, the file system, the display topology, the
encoder cache, the credential vault, and the transfer queue. Returns a list of
checks, each with a level (Ok / Attention / Problem), a **finding** (what is true
right now) and, when something is wrong, **what to do about it**.

Two rules it lives by:

- **Nothing here changes anything.** No host is started, no FFmpeg is launched, no
  network call is made. Diagnostics must never alter what it is diagnosing, and it
  has to work on a machine where something is already broken.
- **A problem always names its fix.** A diagnostic that reports trouble without
  saying what to do just moves the puzzle. The whole value of this module is that a
  novice can read the output and know the next step.

`Locations()` answers the other half of the same question — where each kind of file
actually is on this machine — because "open the folder and look" is a legitimate
answer that no amount of reporting replaces.

The UI's Diagnostics page and the command line both render exactly this, so the two
can never disagree.

## SupportBundle

One zip containing the session journals, integrity records, application logs, the
tail of the encoder's own output, and system information.

**No video and no secrets.** That is not a convention — it is enforced here and
proven by a test that plants a secret in the vault and then searches the produced
bundle for it. A support bundle is something people email; it has to be safe to
email.
