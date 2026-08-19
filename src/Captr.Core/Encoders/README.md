# Encoding

Everything that decides *what we ask FFmpeg to do*: which capture sources, how they
are stitched into one canvas, and the exact argument vector of the encoder process.
Nothing in this folder starts a process — that is `Supervision/`'s job. Keeping this
folder pure (no I/O, no side effects) is what makes the golden-file tests possible.

How a recording becomes an argument vector:

```
resolved displays ──► ArrangementPlanner ──► ArrangementPlan (side-by-side or grid,
                                             pad targets, canvas size)
                                │
RecordingPlan  (sources, fps, encoder args, overlay, folders)
                                │
                     FfmpegArgumentBuilder
                     ├── FilterGraphBuilder   (ddagrab → hwdownload → pad → stack → drawtext)
                     └── DrawTextEscaper      (the overlay-text escaping SPEC §5 warns about)
                                ▼
                     string[]  — one argument per element, NEVER a joined command
                                 line (SPEC §5: argument vector, no concatenation)
```

Design decisions a reader should know:

- **One capture path, not two.** Frames are always downloaded to system memory
  (`hwdownload`) before padding/stacking/encoding. A GPU-resident fast path exists in
  theory for single-display-into-NVENC, but on hybrid-GPU laptops the display often
  sits on the iGPU while NVENC lives on the dGPU, and the direct path fails in ways
  that depend on cabling. One path that always works beats two paths where one
  sometimes doesn't — reliability is design priority #1 (SPEC §1).
- **The capture filter is `ddagrab`** — FFmpeg's DXGI Desktop Duplication source.
  Never GDI (`gdigrab`): it silently omits hardware-composited content (SPEC §1).
  `build/fetch-ffmpeg.ps1` asserts the bundled build has it.
- **Segments are cut by the encoder itself** (`-f segment` + clock alignment +
  forced keyframes), not by the application — so a crash of Captr never corrupts a
  segment boundary, and segments join later by stream copy without re-encoding.
  Segment splits happen ONLY at keyframes, so `-force_key_frames` is load-bearing:
  without it FFmpeg simply never rolls the file (verified empirically in WP0).
- **Golden files pin the output.** Any change to the generated arguments shows up as
  a reviewable diff in `tests/Captr.Core.Tests/Golden/`. Regenerate deliberately with
  `CAPTR_ACCEPT_GOLDEN=1`; never hand-edit a golden file.

Quality/encoder *selection* (probing hardware, trial encodes, presets) lives in this
folder too but arrives with WP5 — the builder only consumes its result
(`EncoderSettings`), so selection changes never disturb the golden files.
