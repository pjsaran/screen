# Building

## One command

```powershell
pwsh build/build.ps1
```

Seven steps, in this order, each failing loudly:

| # | Step | What it does |
|---|---|---|
| 1 | Tools + FFmpeg | `dotnet tool restore`, then `fetch-ffmpeg.ps1` — downloads the pinned build, verifies its SHA-256, and asserts it actually contains `ddagrab` and the software encoder. |
| 2 | Licence gate | `check-licenses.ps1` — fails on any package licence not on the allow-list, and on drift between reality and the committed third-party report. |
| 3 | Formatting | `dotnet format --verify-no-changes`. |
| 4 | Build | `dotnet build` with warnings as errors. |
| 5 | Tests | Unit tests always; integration tests filtered by category. |
| 6 | Publish | Self-contained `win-x64` into `publish/` — only with `-Publish` or `-Installer`. |
| 7 | Installer | `make-installer.ps1` — only with `-Installer`. |

### Options

| | |
|---|---|
| `-Configuration Debug` | Default is Release. |
| `-SkipTests` | Build without running tests. For a fast inner loop only — never for anything you are about to ship. |
| `-SkipFormat` | Skip the formatting gate. |
| `-Full` | Also run the `Display`, `Gpu`, and `Soak` categories. Needs a real desktop and a real GPU. |
| `-Publish` | Produce `publish/`. |
| `-Installer` | Produce `artifacts/captr-setup-<version>.exe` (implies `-Publish`). |

### Always build the installer through `build.ps1 -Installer`
`make-installer.ps1` **packages** `publish/`; it never builds it. Running it on its own
therefore ships whatever happened to be published last — which during development is
usually days old, because the version in `Version.props` does not change between
releases and so a version check cannot tell the difference. The symptom is an installer
that installs and runs perfectly and contains none of your work.

`make-installer.ps1` now refuses when anything under `src/` is newer than the published
binaries, and names the files. If you see that message, the fix is one of:

```powershell
pwsh build/build.ps1 -Installer    # build, test, publish, package — the normal route
pwsh build/build.ps1 -Publish      # refresh publish/ only, then run make-installer.ps1
```
| `-TestFilter` | Override the integration-test filter. Default `Category=Ffmpeg`. |

## Running one part at a time

```powershell
dotnet build Captr.slnx                                  # compile only
dotnet format Captr.slnx                                 # fix formatting
pwsh build/check-licenses.ps1                            # licence gate
pwsh build/fetch-ffmpeg.ps1                              # (re)fetch FFmpeg
pwsh build/make-icons.ps1                                # regenerate icons
pwsh build/tools/normalise-line-endings.ps1              # fix UTF-8/CRLF drift
```

For tests, see [Testing](testing.md).

## The gates, and why each exists

### Warnings are errors

`TreatWarningsAsErrors` with `AnalysisLevel=latest-recommended` and
`EnforceCodeStyleInBuild`. There are no expected warnings in this build, so any
warning is a real finding. Suppressing one requires a comment saying why, in the
suppression itself.

### Banned APIs

`BannedSymbols.txt` is read by `BannedApiAnalyzers` and mechanically forbids
sync-over-async (`Task.Wait`, `.Result`) and the plaintext-secret APIs. These are
the two mistakes that are easy to make and expensive to find later — a deadlock
under load, and a secret in a string that outlives its use.

### The licence gate

Captr must be redistributable commercially without doubt. `check-licenses.ps1`
enumerates every package's licence, fails on anything outside the allow-list, and
compares the result with the committed `build/licenses/THIRD-PARTY.md`. **Adding a
package means regenerating and committing that report**, which is the point: a
licence change cannot slip in unnoticed.

FFmpeg is an **LGPL** build, shipped as a separate child process and never linked.
See [ffmpeg-source-offer.md](../ffmpeg-source-offer.md) for the obligations that
carries and [Upgrading FFmpeg](upgrading-ffmpeg.md) for moving it forward.

### Formatting, UTF-8, and CRLF

`dotnet format --verify-no-changes` fails the build on any drift. The repository is
UTF-8 **without** a BOM with **CRLF** endings; tools that edit files in bulk break
this silently. `build/tools/normalise-line-endings.ps1` fixes it in one pass.

## The version

`Version.props` is the only place a version number is ever typed. Everything else
derives from it — assembly, file, and informational versions; the installer's
version and file name; the build identity on the Diagnostics page. Change it with:

```powershell
pwsh build/set-version.ps1 -Bump minor
```

See [Releasing](releasing.md).

## Continuous integration

`.github/workflows/ci.yml` runs the same gates on every push: licence gate,
formatting, warnings-as-errors build, unit tests, the CI-safe integration
categories (`Ffmpeg` and `Chaos`, both driven by synthetic `lavfi` inputs), then
publish and installer. It uploads `artifacts/**`.

Categories needing a real desktop (`Display`), a real GPU (`Gpu`), or patience
(`Soak`) cannot run on a hosted runner. They run locally with
`pwsh build/build.ps1 -Full`, and the results belong in the
[release verification](release-verification.md) checklist.

Signing is skipped automatically when the signing environment variables are absent,
with a prominent warning. **The build must always succeed unsigned** — a contributor
without a certificate has to be able to build.

## Common build failures

| Symptom | Cause |
|---|---|
| `Unable to copy … Captr.App.exe … used by another process` | Captr is running. Kill both `Captr.App.exe` processes (the UI and the `--host` one). |
| `Formatting differences found` | Run `dotnet format Captr.slnx`, then `pwsh build/tools/normalise-line-endings.ps1`. |
| Licence gate fails after adding a package | Regenerate and commit `build/licenses/THIRD-PARTY.md`; if the licence is not on the allow-list, find another package. |
| FFmpeg checksum mismatch | The pinned asset changed upstream, or the download was corrupted. See [Upgrading FFmpeg](upgrading-ffmpeg.md); do not just update the hash. |
