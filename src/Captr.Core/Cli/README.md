# Cli

Every command the `captr` executable offers (SPEC §10). The logic lives here — the
`Captr.Cli` project is one `Program.cs` calling `CliApplication.RunAsync` — so the
whole surface is unit-testable and shared.

Design rules from SPEC §10, encoded here:

- **Exit codes are meaningful and documented** (`ExitCodes`): `0` success,
  `10` idle, `11` paused, `1` error, `2` bad usage, `3` host unreachable.
  A scheduled task can branch on them.
- **Idempotent by design**: `start` while recording and `stop` while idle both
  report the actual state and exit `0` — schedulers fire twice more often than
  anyone expects.
- **Errors to stderr, results to stdout**, and every command has `--json` for
  machine consumption.
- Commands that summon work (`start`, `recover`) launch a host when none is
  running; pure queries (`status`, `stop`, `pause`, `resume`) treat "no host" as
  "idle" rather than spawning one. `recordings` and `verify` read the working
  folder directly — they need no host at all.
- Secrets are NEVER accepted as arguments (visible to every process on the
  machine, SPEC §7); credential provisioning reads stdin or prompts masked.
- `doctor` renders `Captr.Core.Diagnostics.HealthReport` — the SAME checks the
  Diagnostics page shows, so the window and the terminal can never disagree about
  whether a machine is healthy. It exits `1` on a Problem-level finding, which is
  what makes it usable in a deployment check.
