# Captr developer guide

For anyone building or changing Captr.

## Read in this order

1. **[Environment setup](environment-setup.md)** — what to install, and your first
   green build.
2. **[Architecture](architecture.md)** — the processes, one recording end to end,
   and where each concern lives.
3. **[Building](building.md)** — the pipeline, its gates, and how to run one part
   at a time.
4. **[Testing](testing.md)** — the suite, its categories, and what covers which
   requirement.

## When you need it

- **[Releasing](releasing.md)** — set a version, build, sign, tag, publish.
- **[Upgrading FFmpeg](upgrading-ffmpeg.md)** — moving the pinned build forward
  without breaking capture or the licence position.
- **[Design decisions](design-decisions.md)** — where Captr departs from the
  specification, and why. Read this before "fixing" something that looks odd.
- **[Release verification](release-verification.md)** — the checks that need real
  machines and cannot be automated here.

## The rules that shape this codebase

These are not style preferences. Each one exists because breaking it produces a
specific, bad outcome.

**Never a Windows Service.** A service runs in session 0, where Desktop
Duplication records black. The prohibition is stated in code where someone would
otherwise be tempted.

**Desktop Duplication first; GDI only where Desktop Duplication does not exist.**
GDI capture is slow, tears, and misses hardware-accelerated content, so it is never
a *preference* and never a mid-session fallback. It is the last rung of the
start-time capture ladder purely because virtual desktops — AWS WorkSpaces, some
Citrix and VM hosts — have no Desktop Duplication at all, and there the choice is
GDI or no recording. Diagnostics says plainly when the compatibility path is in
use.

**The recording outlives everything watching it.** FFmpeg is deliberately not in a
job object, its progress goes to a *file* rather than a pipe, and the journal is
flushed to physical disk on every append. A crashed host, a killed UI, or a power
cut must cost seconds of footage, never a session.

**Never report a recording as continuous when it is not.** Gaps are journaled as
they happen and surfaced immediately, in the UI and in the integrity record.

**No secret ever reaches a file we write.** Not settings, not logs, not the support
bundle, not a settings export, and never a command-line argument. Secrets live in
Windows Credential Manager, DPAPI-bound to the user and the machine, and
`Captr.Core.Secrets` is the only module allowed to hold one. There is a test that
plants a secret and greps the support bundle for it.

**Licensing is mechanical, not remembered.** `build/check-licenses.ps1` fails the
build on a package licence that is not on the allow-list, and the committed
third-party report must match reality. FFmpeg is an LGPL build, shipped as a child
process and never linked.

**Every folder explains itself.** Each folder under `src/` has a `README.md` saying
what it owns and where to start reading. Every public type carries a comment saying
what it owns and what breaks if it is wrong. Keep that up — it is the difference
between a codebase someone can join and one they cannot.
