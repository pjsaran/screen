# Bundled FFmpeg: licence obligations and source offer

Captr bundles FFmpeg as a **separate child process** — it is never linked into the
application. The bundled build is currently the **GPL** build (it includes
libx264, verified mechanically at fetch time). That is a deliberate, recorded
decision for an **internal-only deployment**: the GPL's obligations attach on
distribution ("conveying"), and copies made for use within the organisation are
not conveyed, so no obligation activates in normal use. The trade is worth it
because libx264's true constant-quality mode makes the software fallback behave
like the GPU encoders instead of a fixed bitrate.

**If this software is ever distributed outside the organisation**, either
(a) honour the GPL for the bundled FFmpeg — the source offer below already does
this, and the arms-length process boundary keeps Captr's own code out of scope —
or (b) repoint `build/ffmpeg.lock.json` at the `-lgpl` asset of the same release
tag; the encoder catalog reads what the shipped binary contains and degrades to
libopenh264 with no code change. (See `docs/developer-guide/design-decisions.md`.)

## Exact build shipped

The pinned build is recorded in two committed places and one shipped file:

- `build/ffmpeg.lock.json` — upstream repository (`BtbN/FFmpeg-Builds`), release
  tag, asset name, and SHA-256 the fetch verifies against.
- `artifacts/release.json` — the build id and hash for each release.
- `<install dir>\ffmpeg\capabilities.json` — the same identifiers on disk beside
  the binary, so a running installation can state exactly what it ships.

The binary is shipped **bit-for-bit unmodified** from the upstream asset, with one
documented exception: at release time the executables are Authenticode-signed
(signing modifies the file; SPEC §2 documents this as a permitted, recorded
change — an unsigned screen-capturing exe with a well-known name is a textbook
endpoint-protection detection).

## Written offer of corresponding source

The bundled build is licensed under the GNU General Public License v3 (the GPL
build of FFmpeg incorporates libx264, which is GPL). The licence texts ship in
`<install dir>\ffmpeg\licenses\`.

The complete corresponding source code for the exact bundled build is publicly
available from the upstream build project at:

> https://github.com/BtbN/FFmpeg-Builds
> (release tag as recorded in `build/ffmpeg.lock.json`; the build scripts in that
> repository, plus the FFmpeg source tag they reference, constitute the
> corresponding source)

Should that URL become unavailable: **we offer to provide the complete
corresponding source of the bundled FFmpeg build to any recipient of this
software, at no more than the cost of physically performing the distribution.**
Request it through the contact address in the application's About page. This
offer is valid for at least three years from the distribution date recorded in
`release.json`.
