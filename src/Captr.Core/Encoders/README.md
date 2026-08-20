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
EncoderSelector    first proven winner wins; cached against GPU identity + driver
                   version + canvas (EncoderCache), and a cached winner still gets
                   one confirmation trial before it is trusted
        │
SizeEstimator      measures GB/hour from a real trial — never a hardcoded table
QualityPresets     named presets → per-encoder quality arguments, plus the numeric
                   override for people who want direct control
```

## Design decisions a reader should know

- **One capture path, not two.** Frames are always downloaded to system memory
  (`hwdownload`) before padding/stacking/encoding. A GPU-resident fast path exists
  in theory for single-display-into-NVENC, but on hybrid-GPU laptops the display
  often sits on the iGPU while NVENC lives on the dGPU, and the direct path then
  fails in ways that depend on cabling. One path that always works beats two where
  one sometimes doesn't — reliability is design priority #1.
- **The capture filter is `ddagrab`** — FFmpeg's DXGI Desktop Duplication source.
  Never GDI (`gdigrab`): it silently omits hardware-composited content (SPEC §1),
  which is the worst kind of failure because the file looks fine.
  `build/fetch-ffmpeg.ps1` asserts the bundled build has it.
- **Segments are cut by the encoder itself** (`-f segment`, clock-aligned, forced
  keyframes), not by the application — so a crash of Captr never corrupts a segment
  boundary, and segments join later by stream copy. The segment muxer only cuts at
  keyframes, so `-force_key_frames` is load-bearing: without it FFmpeg never rolls
  the file at all (verified empirically).
- **Clusters are capped at 2 seconds** so a killed encoder loses less: see
  `EncodingConstants.ClusterTimeLimitMilliseconds` for the measurement behind it.
- **Golden files pin the output.** Any change to the generated arguments shows up
  as a reviewable diff in `tests/Captr.Core.Tests/Golden/`. Regenerate deliberately
  with `CAPTR_ACCEPT_GOLDEN=1`; never hand-edit a golden file.
