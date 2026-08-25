# Upgrading the bundled FFmpeg

Captr ships a **pinned** FFmpeg build. It is downloaded at build time, verified
against a committed checksum, and interrogated to prove it actually contains what
Captr needs. Upgrading it is a deliberate, checked operation — not a version number
you edit.

Everything about the pin lives in one file: **`build/ffmpeg.lock.json`**.

```jsonc
{
  "repository":       "BtbN/FFmpeg-Builds",
  "releaseTag":       "autobuild-2026-08-18-15-03",
  "assetName":        "ffmpeg-n8.1.2-44-g7c533d0f86-win64-lgpl-8.1.zip",
  "sha256":           "94df4ac3…",
  "buildId":          "ffmpeg-n8.1.2-44-g7c533d0f86-win64-lgpl",
  "requiredFilters":  ["ddagrab", "hwdownload", "pad", "hstack", "xstack", "drawtext"],
  "requiredEncoders": ["hevc_nvenc", "h264_nvenc", "hevc_qsv", "h264_qsv", "hevc_amf", "h264_amf"],
  "optionalEncoders": ["libopenh264"],
  "requiredMuxers":   ["segment", "matroska"]
}
```

## Two rules that are not negotiable

**It must be an `-lgpl` build.** GPL builds carry libx264 and libx265, and with them
redistribution obligations that would change Captr's licence position entirely. The
asset name is checked, and so is the licence text that ships beside the binary.

**Never pin the rolling `latest` tag.** A rolling tag means the bytes under the pin
change without the pin changing, which defeats the entire point of a checksum. Pin a
dated `autobuild-YYYY-MM-DD-HH-MM` tag.

## The upgrade

1. **Pick a build.** Go to
   [BtbN/FFmpeg-Builds releases](https://github.com/BtbN/FFmpeg-Builds/releases) and
   choose a dated `autobuild-*` release. Find the `win64-**lgpl**` asset.

2. **Get its checksum from upstream**, not by hashing your download — the point is to
   detect a corrupted or substituted download, and hashing what you got proves
   nothing. Each release publishes `checksums.sha256`.

3. **Update `ffmpeg.lock.json`**: `releaseTag`, `assetName`, `sha256`, and `buildId`
   (the asset name without the `.zip` and without the trailing FFmpeg-version
   suffix).

4. **Fetch and verify:**

   ```powershell
   Remove-Item tools/ffmpeg -Recurse -Force
   pwsh build/fetch-ffmpeg.ps1
   ```

   This downloads the asset, fails hard on a checksum mismatch, extracts
   `ffmpeg.exe` and `ffprobe.exe`, and then asks the binary itself whether it has:

   - **`ddagrab`** — FFmpeg's Desktop Duplication capture. Generic builds sometimes
     omit it, and without it Captr cannot capture at all. A missing `ddagrab` is a
     hard failure.
   - every hardware encoder Captr probes for at runtime;
   - **`libopenh264`** — optional, and it decides whether there is a software
     fallback. LGPL builds have no libx264, so this is the only software encoder
     available. The answer is recorded in `tools/ffmpeg/capabilities.json`, never
     assumed.

5. **Run the full build**, including the parts that use real capture and real
   encoders:

   ```powershell
   pwsh build/build.ps1 -Full
   ```

   The `Display` and `Gpu` categories are the ones that matter here: they are what
   proves the new build still captures a real desktop and still drives the hardware
   encoders on this machine.

6. **Record a real recording and watch it.** An FFmpeg upgrade can change filter
   behaviour in ways no test asserts — colour, scaling of a mismatched-resolution
   multi-monitor stitch, the exact segment boundaries. Record two displays for a few
   minutes, play the result, and check the join between segments.

7. **Check the licence texts still ship.** `fetch-ffmpeg.ps1` stages them into
   `tools/ffmpeg/licenses`, and the publish step copies that folder beside the
   binary. Confirm it is there in `publish/ffmpeg/licenses`.

8. **Commit** the updated `ffmpeg.lock.json`. Nothing under `tools/` is committed —
   it is fetched.

## Afterwards

- The new build id flows automatically into `captr version`, the Diagnostics page,
  and `release.json`.
- **The encoder cache invalidates itself.** The FFmpeg build id is part of the cache
  fingerprint, so the first recording after an upgrade re-proves the encoder rather
  than trusting a result measured on the old build.
- [`docs/ffmpeg-source-offer.md`](../ffmpeg-source-offer.md) references the pin by
  path rather than by version, so it stays correct without editing. Re-read it if
  the upstream project ever moves.

## If a required capability is missing

`fetch-ffmpeg.ps1` fails and names what is absent. Do not work around it by relaxing
the lock file — pick a different build. A build without `ddagrab` cannot record, and
a build without `libopenh264` leaves machines with no working GPU encoder unable to
record at all.
