# Encoders

Everything that decides *what we ask FFmpeg to do*: which capture sources, how they
are stitched into one canvas, which encoder is actually capable of it, and the exact
argument vector of the encoder process. Nothing here starts a recording — that is
`Supervision/`'s job. Keeping this folder pure (no side effects) is what makes the
golden-file tests possible.

## How a recording becomes an argument vector

```
resolved displays ──► ArrangementPlanner ──► ArrangementPlan (side-by-side or grid,
                                             pad targets, canvas size)
                                │
RecordingPlan  (sources, fps, encoder settings, overlay, folders)
                                │
                     FfmpegArgumentBuilder
                     ├── FilterGraphBuilder   ddagrab → hwdownload → pad → stack → drawtext
                     └── DrawTextEscaper      the overlay escaping SPEC §5 warns about
                                ▼
                     string[]  — one argument per element, NEVER a joined command
                                 line (SPEC §5: argument vector, no concatenation)
```

## How an encoder gets chosen

```
EncoderCatalog     HEVC hardware → H.264 hardware → software, filtered by what the
                   bundled build actually contains (capabilities.json)
        │
EncoderTrial       a SHORT ENCODE OF THE REAL CANVAS per candidate. Appearing in
                   ffmpeg's encoder list proves nothing: a machine can advertise a
                   vendor encoder with no matching GPU and write an empty file.
                   A candidate passes only if the output is non-empty and probes
                   back with the expected dimensions and a non-zero packet count.
        │
EncoderSelector    first proven winner wins, and the SAME trial measures how fast it
                   writes; both are cached against GPU identity + driver version +
                   canvas + the encoding settings (EncoderCache)
        │
```

The three things a user chooses live in their own catalogues, one per question:

| Question | Type | Options |
|---|---|---|
| How often is the screen sampled? | `CaptureRates` | 5 · 10 · 15 · 20 · 24 · 30 · 45 · 60 FPS |
| How much CPU may the encoder spend per frame? | `SpeedPresets` | ultrafast → fast |
| How good must the picture look? | `QualityLevels` | lossless (CRF 0) → compact (CRF 28) |

`QualityLevels.BuildEncoderArguments` is the only place the last two combine, and
every encoder in `EncoderCatalog` must appear in both `QualityLevels` and
`SpeedPresets` — a unit test fails if one is added without the other.

## Design decisions a reader should know

- **One capture path, not two.** Frames are always downloaded to system memory
  (`hwdownload`) before padding/stacking/encoding. A GPU-resident fast path exists
  in theory for single-display-into-NVENC, but on hybrid-GPU laptops the display
  often sits on the iGPU while NVENC lives on the dGPU, and the direct path then
  fails in ways that depend on cabling. One path that always works beats two where
  one sometimes doesn't — reliability is design priority #1.
- **The capture filter is `ddagrab`** — FFmpeg's DXGI Desktop Duplication source.
  `build/fetch-ffmpeg.ps1` asserts the bundled build has it. GDI (`gdigrab`) is
  avoided as a first choice because it can omit hardware-composited content
  (SPEC §1) and costs real CPU — but it exists as a **last-resort fallback**:
  on virtual desktops (AWS WorkSpaces, some VMs and RDP hosts) the display
  driver cannot create the D3D11 device ddagrab needs, and before the fallback
  those machines could not record *at all*. `EncoderSelector` proves capture the
  same way it proves encoders — ddagrab first, gdigrab only when capture itself
  is what failed — and the winning `CaptureMethod` is cached with the encoder
  and reported by Diagnostics. On such machines the desktop is software-composed
  anyway, so GDI's blind spot is largely moot there.
- **Segments are cut by the encoder itself** (`-f segment`, clock-aligned, forced
  keyframes), not by the application — so a crash of Captr never corrupts a segment
  boundary, and segments join later by stream copy. The segment muxer only cuts at
  keyframes, so `-force_key_frames` is load-bearing: without it FFmpeg never rolls
  the file at all (verified empirically).
- **Clusters are capped at 2 seconds** so a killed encoder loses less: see
  `EncodingConstants.ClusterTimeLimitMilliseconds` for the measurement behind it.
- **A cache hit skips the trial entirely.** Probing used to cost a 2-second
  confirmation encode plus an 8-second size measurement on *every* start, which is
  what made pressing Record feel broken. Once the fingerprint matches, the cached
  winner and its measured rate are used immediately and recording starts at once.
  The one risk a fingerprint cannot see — a driver that broke without changing its
  version — is caught by the recording itself: a session that dies in its first
  30 seconds clears the cache (`RecordingSession.ForgetCachedEncoderIfItFailed…`),
  so the next start re-probes. One failed start beats ten seconds on every start.
- **Golden files pin the output.** Any change to the generated arguments shows up
  as a reviewable diff in `tests/Captr.Core.Tests/Golden/`. Regenerate deliberately
  with `CAPTR_ACCEPT_GOLDEN=1`; never hand-edit a golden file.
