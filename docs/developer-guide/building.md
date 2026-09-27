# Building

## One command

```powershell
pwsh build/build.ps1
```

Seven steps, in this order, each failing loudly:

| # | Step | What it does |
|---|---|---|
| 1 | Tools + FFmpeg | `dotnet tool restore`, then `fetch-ffmpeg.ps1` — downloads the pinned build, verifies its SHA-256, asserts it actually contains `ddagrab` and the software encoder, and records a digest of each executable that the application checks before every use. |
| 2 | Licence gate | `check-licenses.ps1` — fails on any package licence not on the allow-list, and on drift between reality and the committed third-party report. |
| 3 | Formatting | `dotnet format --verify-no-changes`. |
| 4 | Build | `dotnet build` with warnings as errors. |
| 5 | Tests | Unit tests always; integration tests selected by excluding what this step cannot run — see [test selection](#test-selection). |
| 6 | Publish | Self-contained `win-x64` into `publish/`, with the licence notices under `publish/licenses/`, Captr's PDBs moved to `artifacts/symbols/`, and every shipped file signed (or the plan printed, unsigned) — then the tests that drive the published payload. Only with `-Publish`, `-Installer`, or `-Full`. |
| 7 | Installer | `make-installer.ps1` — only with `-Installer`. |

### Options

| | |
|---|---|
| `-Configuration Debug` | Default is Release. |
| `-SkipTests` | Build without running tests. For a fast inner loop only — never for anything you are about to ship. |
| `-SkipFormat` | Skip the formatting gate. |
| `-Full` | Also run the `Display`, `Gpu`, and `Soak` categories, after publishing (it implies a publish). Needs a real desktop and a real GPU. |
| `-Publish` | Produce `publish/`. |
| `-Installer` | Produce `artifacts/captr-setup-<version>.exe` (implies `-Publish`). |
| `-TestFilter "<filter>"` | Replace the step 5 integration-test selection entirely, e.g. `-TestFilter "Category=Installer"`. |
| `-RequireSigning` | Fail instead of warning when no signing method is configured. `new-release.ps1` passes it; see [Releasing](releasing.md#code-signing). |

### Test selection

Integration tests are chosen by **excluding** what a step cannot run, never by
listing what it can — an include-list once silently skipped every test that carried
no category, fourteen of them, while this documentation cited them as coverage.
Every integration test class must declare one known category (`TestCategoryTests`
fails otherwise), and a category is a promise about what the test needs.

| Category | Needs | Runs |
|---|---|---|
| `Os` | Windows itself — pipes, ACLs, power requests, PowerShell scripts — but no FFmpeg, display, or GPU | Step 5, always |
| `Ffmpeg` | The bundled FFmpeg, driven with synthetic `lavfi` inputs | Step 5, always |
| `Chaos` | The same, plus randomised kills and deliberate corruption | Step 5, always |
| `Published` | `publish/captr.exe` | Step 6, after publishing |
| `Display`, `Gpu`, `Soak` | A real desktop, a real GPU, and (for `Soak`) patience; several drive the published payload | Step 6, with `-Full` only |
| `Installer` | The built installer; it installs and uninstalls Captr | Only on purpose: `-TestFilter "Category=Installer"` after `-Installer` |

So step 5 runs `Category!=Published&Category!=Installer&Category!=Display&Category!=Gpu&Category!=Soak`,
and a test someone forgets to categorise runs anyway (and fails the category test).
The tests that drive `publish/` refuse to run against a payload older than the
source, naming the fix, rather than silently testing yesterday's build.

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

The same check covers `Version.props`, `Directory.Build.props`, and
`Directory.Packages.props`: a bumped package or version is a different payload too.

### The pinned Inno Setup compiler

`make-installer.ps1` installs Inno Setup 6.7.3 per-user into `tools/innosetup` from
a download it checks against a pinned SHA-256, and checks the installer's own exit
code. It then checks `ISCC.exe` and `ISCmplr.dll` against their pinned SHA-256
**every time**, because the compiler has no version resource: an Inno Setup already
sitting in `tools/` — installed by hand, or a different version — used to be trusted
as it was, and it is what compiles (and signs) what ships. If it does not match, the
message says to delete `tools/innosetup` and run again.

## Running one part at a time

```powershell
dotnet build Captr.slnx                                  # compile only
dotnet format Captr.slnx                                 # fix formatting
pwsh build/check-licenses.ps1                            # licence gate
pwsh build/check-licenses.ps1 -Update                    # rewrite THIRD-PARTY.md after a deliberate dependency change
pwsh build/fetch-ffmpeg.ps1                              # (re)fetch FFmpeg
pwsh build/Sign-Artifacts.ps1 -Path publish -DryRun      # what signing would do; no certificate needed
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
compares the result with the committed `build/licenses/THIRD-PARTY.md`. **Adding,
removing, or upgrading a package means regenerating that report with
`pwsh build/check-licenses.ps1 -Update` and committing it**, which is the point: a
licence change cannot slip in unnoticed. The same report ships with the product, in
`licenses\THIRD-PARTY.md`.

The pinned FFmpeg is the **GPL** build (it carries libx264), shipped as a separate
child process and never linked. That is a recorded decision for an internal-only
deployment, not an oversight — see
[design decisions](design-decisions.md) — and the LGPL build of the same tag is the
way back if Captr is ever distributed outside the organisation. See
[ffmpeg-source-offer.md](../ffmpeg-source-offer.md) for the obligations that
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

It accepts only plain `MAJOR.MINOR.PATCH`. A pre-release suffix such as
`0.2.0-beta.1` is refused, because Windows file versions and Inno Setup's version
must be numeric and every later build would fail. See [Releasing](releasing.md).

## Smart App Control on a development machine

A Windows 11 PC with **Smart App Control** on blocks code that carries no valid
signature — and a development build is unsigned. On the machine Captr is developed
on, it refused to load an unsigned `Captr.Core.dll` outright. Smart App Control
has no per-file exceptions, so either build and test on a machine where it is off,
or configure a real signing certificate ([Releasing](releasing.md#code-signing)) so
`build.ps1` signs what it publishes. This is also why a release signs every DLL it
ships, third-party ones included: a user's PC may have Smart App Control on.

## Continuous integration

`.github/workflows/ci.yml` runs the same gates on every push and pull request:
licence gate, formatting, warnings-as-errors build, unit tests, the integration
tests selected by the same exclusion as step 5 (`Os`, `Ffmpeg`, `Chaos`, and anything
uncategorised), then publish and installer, a signing dry run, and the `Published`
tests against the payload it just built. It uploads `artifacts/**`. Actions are
pinned to commit SHAs, the token is read-only, and the job times out after 90
minutes.

Categories needing a real desktop (`Display`), a real GPU (`Gpu`), or patience
(`Soak`) cannot run on a hosted runner. They run locally with
`pwsh build/build.ps1 -Full`, and the results belong in the
[release verification](release-verification.md) checklist. `Installer` runs only
on purpose ([Testing](testing.md#the-installer-suite)).

Signing happens in CI only where the repository's signing secrets exist; there,
every shipped file is verified and an unsigned one fails the job. Without them —
pull requests, forks — the build is unsigned, with a prominent warning. **The build
must always succeed unsigned** — a contributor without a certificate has to be able
to build.

## Common build failures

| Symptom | Cause |
|---|---|
| `Unable to copy … Captr.App.exe … used by another process` | Captr is running. Kill both `Captr.App.exe` processes (the UI and the `--host` one). |
| `Formatting differences found` | Run `dotnet format Captr.slnx`, then `pwsh build/tools/normalise-line-endings.ps1`. |
| Licence gate fails after adding a package | Regenerate and commit `build/licenses/THIRD-PARTY.md`; if the licence is not on the allow-list, find another package. |
| FFmpeg checksum mismatch | The pinned asset changed upstream, or the download was corrupted. See [Upgrading FFmpeg](upgrading-ffmpeg.md); do not just update the hash. |
| `The published payload in … was built at … and these source files have changed since` | `publish/` is stale. `pwsh build/build.ps1 -Installer` (or `-Publish`). |
| `…\tools\innosetup\ISCC.exe is not the pinned Inno Setup 6.7.3` | Delete `tools/innosetup` and run again; the pinned version is installed. |
| `'0.2.0-beta.1' is not a valid version` | Pre-release suffixes are not supported; use `MAJOR.MINOR.PATCH`. |
| `Refusing to sign …\ffmpeg.exe: it is not the pinned FFmpeg build` | `publish/` holds an FFmpeg other than the one fetched and verified. Run `pwsh build/fetch-ffmpeg.ps1` and publish again. |
| Tests fail with a file-load or "blocked" error on a PC with Smart App Control | See [Smart App Control](#smart-app-control-on-a-development-machine). |
