# Naming

Turns the user's output naming pattern (e.g. `{machine} {date} {start}-{end}`) into a
safe Windows file name, and guarantees that no recording ever overwrites another
(SPEC §7).

- **OutputNamer** — substitutes the tokens, sanitises the result against illegal
  characters and reserved device names (`CON`, `PRN`, `COM1`…), and resolves
  collisions by suffixing ` (2)`, ` (3)`, … — never by overwriting.

Rules that matter:

- Times in file names are shown in the session's own local timezone (the journal
  stores UTC plus the timezone; file names are for humans).
- Sanitisation must never produce an empty or reserved name — there is always a
  usable fallback.
- Collision handling appends a suffix *before the extension* and probes until a free
  name is found. Overwriting is not an option anywhere in this folder.
