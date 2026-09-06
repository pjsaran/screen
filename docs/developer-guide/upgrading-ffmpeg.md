# Upgrading the bundled FFmpeg

Captr ships a **pinned** FFmpeg build. It is downloaded at build time, verified
against a committed checksum, and interrogated to prove it actually contains what
Captr needs. Upgrading it is a deliberate, checked operation — not a version number
you edit.

Everything about the pin lives in one file: **`build/ffmpeg.lock.json`**.

```jsonc
{
  "repository":       "BtbN/FFmpeg-Builds",
  "releaseTag":       "autobuild-2026-08-31-13-27",
  "assetName":        "ffmpeg-n8.1.2-50-g1a748fe2cd-win64-gpl-8.1.zip",
  "sha256":           "273abb45…",
  "buildId":          "ffmpeg-n8.1.2-50-g1a748fe2cd-win64-gpl",
  "licenceFlavor":    "gpl",
  "requiredFilters":  ["ddagrab", "hwdownload", "pad", "hstack", "xstack", "drawtext"],
  "requiredEncoders": ["hevc_nvenc", "h264_nvenc", "hevc_qsv", "h264_qsv", "hevc_amf", "h264_amf"],
  "optionalEncoders": ["libx264", "libopenh264"],
  "requiredMuxers":   ["segment", "matroska"]
}
```

## Three rules that are not negotiable

**Pin a month-end tag only.** BtbN/FFmpeg-Builds publishes a build every day but
keeps daily builds for only about two weeks; after that only the **last build of each
month** survives. A mid-month pin is a trap: it works on the machine that fetched it
(the zip is cached in `tools/ffmpeg`, which git ignores) and then returns 404 for every
other machine and for CI once upstream prunes it. That is exactly what happened with
`autobuild-2026-08-18-15-03` — fresh clones compiled but could not fetch FFmpeg, so
the app had no encoder. On the releases page, a month-end tag is the one dated the
last day of the month (`autobuild-2026-08-31-*`, `autobuild-2026-09-30-*`, …); the
older entries on the page are all month-end builds, which is how you can tell.

**Never pin the rolling `latest` tag.** A rolling tag means the bytes under the pin
change without the pin changing, which defeats the entire point of a checksum.

**Match `licenceFlavor` to the asset.** The lock currently pins the **`-gpl`** build
on purpose: this deployment is internal-only, and the GPL build carries libx264,
whose CRF mode gives the software fallback proper constant-quality encoding (see
[design-decisions.md](design-decisions.md)). `fetch-ffmpeg.ps1` checks that the binary
matches the declared flavour, so pasting the wrong asset line fails loudly. If Captr
is ever distributed outside the organisation, switch to the `-lgpl` asset of the same
tag, set `licenceFlavor` to `lgpl`, and remove `libx264` from `optionalEncoders`.

## The upgrade

1. **Pick a build.** Go to
   [BtbN/FFmpeg-Builds releases](https://github.com/BtbN/FFmpeg-Builds/releases) and
   choose a **month-end** `autobuild-*` release. Find the `win64-gpl-8.1` asset (or
   `-lgpl` if the licence posture has changed — see above).

2. **Get its checksum from upstream**, not by hashing your download — the point is to
   detect a corrupted or substituted download, and hashing what you got proves
   nothing. Each release publishes `checksums.sha256`; copy the line for the asset.

3. **Update `ffmpeg.lock.json`**: `releaseTag`, `assetName`, `sha256`, and `buildId`
   (the asset name without the `.zip` and without the trailing FFmpeg-version
   suffix, e.g. `…-win64-gpl-8.1.zip` → `…-win64-gpl`).

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
   - the software encoders listed in `optionalEncoders` — these decide the software
     fallback tier. The answer is recorded in `tools/ffmpeg/capabilities.json`, never
     assumed.

5. **Refresh the licence report.** `build/licenses/THIRD-PARTY.md` names the FFmpeg
   build id, and the licence gate fails on any drift, so regenerate it and review the
   one-line diff:

   ```powershell
   pwsh build/check-licenses.ps1 -Update
   ```

6. **Run the full build**, including the parts that use real capture and real
   encoders:

   ```powershell
   pwsh build/build.ps1 -Full
   ```

   The `Display` and `Gpu` categories are the ones that matter here: they are what
   proves the new build still captures a real desktop and still drives the hardware
   encoders on this machine.

7. **Record a real recording and watch it.** An FFmpeg upgrade can change filter
   behaviour in ways no test asserts — colour, scaling of a mismatched-resolution
   multi-monitor stitch, the exact segment boundaries. Record two displays for a few
   minutes, play the result, and check the join between segments.

8. **Check the licence texts still ship.** `fetch-ffmpeg.ps1` stages them into
   `tools/ffmpeg/licenses`, and the publish step copies that folder beside the
   binary. Confirm it is there in `publish/ffmpeg/licenses`.

9. **Commit** `ffmpeg.lock.json` and `THIRD-PARTY.md`. Nothing under `tools/` is
   committed — it is fetched.

## Afterwards

- The new build id flows automatically into `captr version`, the Diagnostics page,
  and `release.json`.
- **The encoder cache invalidates itself.** The FFmpeg build id is part of the cache
  fingerprint, so the first recording after an upgrade re-proves the encoder rather
  than trusting a result measured on the old build.
- [`docs/ffmpeg-source-offer.md`](../ffmpeg-source-offer.md) references the pin by
  path rather than by version, so it stays correct without editing. Re-read it if
  the upstream project ever moves.

## If the fetch fails with 404

The pinned release has been pruned upstream (see rule one). Re-pin to a month-end
tag following the steps above. `fetch-ffmpeg.ps1` now says this in its error message
rather than leaving a bare "Not Found".

## If a required capability is missing

`fetch-ffmpeg.ps1` fails and names what is absent. Do not work around it by relaxing
the lock file — pick a different build. A build without `ddagrab` cannot record, and
a build with no software encoder at all leaves machines with no working GPU encoder
unable to record.
