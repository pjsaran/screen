# Settings

The user's configuration (SPEC §8) — deliberately small. Only what a user genuinely
needs to choose lives here: displays to record, frame rate, quality, working folder,
naming pattern, retention, destinations, hotkeys, and a few cosmetic behaviours.
Everything else (segment length, supervision thresholds, ballast size…) is a
documented constant in code, NOT a setting — see the `*Constants` classes in the
folder that owns each value.

Pieces:

- **CaptrSettings** — the immutable settings model. One record, versioned with
  `SchemaVersion`.
- **SettingsStore** — loads/saves `%APPDATA%\Captr\settings.json` atomically
  (via `AtomicFile`, so a crash mid-save can never corrupt settings), runs
  migrations on load, validates on save, and provides export/import.
- **SettingsValidator** — every rule that makes settings usable, with a per-field
  message. Recording refuses to start while settings are invalid (SPEC §8), so the
  messages must tell the user exactly what to fix.
- **Migrations/** — forward-only schema migrations. When `SchemaVersion` grows,
  add a migration here and a fixture test in
  `tests/Captr.Core.Tests/Settings/` proving an old file still loads.

- **SettingsChangePolicy** — the SPEC §8 lock. While a recording runs, capture and
  quality settings accept only *degrading* changes (a lower frame rate, a lower
  quality preset, removing a display); each is applied to the live session, rolls a
  new segment, and is journaled. Anything asking the machine for more work is
  refused with a message saying what to do instead. Settings that do not touch the
  encoder — naming, retention, hotkeys, cosmetics — change freely at any time.
  The comparison is against what the session is *actually* running at, not the
  settings file, because automatic frame-rate reduction may already have taken it
  lower.

Two rules worth knowing:

- **No secret ever appears here** (SPEC §7). Destinations reference a credential by
  name; the secret itself lives in Windows Credential Manager via
  `Captr.Core.Secrets`. Settings export states this explicitly in the exported file.
- **Display selection is stored as exclusions** (SPEC §5): a newly attached display
  is recorded by default; forgetting to update settings can never silently drop a
  monitor from the recording.
