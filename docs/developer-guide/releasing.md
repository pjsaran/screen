# Releasing

## The short version

```powershell
pwsh build/new-release.ps1 -Bump minor
```

That bumps the version, commits the bump, builds and tests everything, produces and
signs the installer, verifies the artefacts against their recorded hashes, tags the
commit, pushes, and creates the GitHub release with the installer attached.

Nothing is tagged or published until every earlier step has passed.

## Rehearse it first

```powershell
pwsh build/new-release.ps1 -SkipPublish
```

Everything except tagging, pushing, and GitHub. Use this the first time, and any
time you have changed the packaging.

## The scripts

| Script | Does |
|---|---|
| `build/set-version.ps1` | Sets the version in `Version.props`. Nothing else. |
| `build/build.ps1` | Format, licence gate, build, test, publish, installer. |
| `build/make-installer.ps1` | Inno Setup compile, signing, `SHA256SUMS.txt`, `release.json`. |
| `build/sign.ps1` | Authenticode signing. Skipped with a warning when unconfigured. |
| `build/verify-install.ps1` | Post-install verification on a real machine. |
| `build/new-release.ps1` | All of the above, in the right order, plus tag and publish. |
| `build/create-github-repo.ps1` | One-time: create the GitHub repository and push. |

## Versioning

`Version.props` holds the only version number in the product. Everything derives
from it: assembly, file, and informational versions; the installer's `AppVersion`
and file name; the build identity on the Diagnostics page and in `captr version`.

```powershell
pwsh build/set-version.ps1                  # report the current version
pwsh build/set-version.ps1 -Bump patch      # 0.1.0 -> 0.1.1
pwsh build/set-version.ps1 -Bump minor      # 0.1.1 -> 0.2.0
pwsh build/set-version.ps1 -Version 1.0.0   # exactly this
```

It refuses an invalid version and refuses to go backwards without
`-AllowDowngrade`. It does **not** commit or tag — `new-release.ps1` does that,
after the build has passed, so no commit exists for a version that does not build.

Semantic versioning: **major** for a break, **minor** for a feature, **patch** for a
fix. Two things in particular are breaking changes for a user:

- a **settings schema** bump without a migration (there must always be a migration
  — see `src/Captr.Core/Settings/Migrations/`);
- an **IPC protocol** bump (`IpcProtocol.Version`), which makes an old CLI refuse to
  talk to a new host. That is intended behaviour, but it is a break.

## Signing

Set two environment variables before releasing:

```powershell
$env:CAPTR_SIGN_PFX      = 'C:\secure\captr-codesign.pfx'
$env:CAPTR_SIGN_PASSWORD = '<the pfx password>'
```

`sign.ps1` signs the published binaries and the installer with SHA-256 and a
timestamp server, so signatures stay valid after the certificate expires.

**Without them the build still succeeds**, prints a prominent warning, and records
`"signed": false` in `release.json`. `new-release.ps1` repeats the warning before
publishing. That is deliberate: a contributor without a certificate must still be
able to build and test the real installer.

For a local end-to-end test of the signed path, a self-signed certificate is
enough; `verify-install.ps1 -SkipSignatureCheck` covers the unsigned case.

> Signing is the one documented modification to the bundled FFmpeg binaries. An
> unsigned screen-capturing executable with a well-known name is a textbook
> endpoint-protection detection, so the executables are signed at release time. This
> is recorded in [ffmpeg-source-offer.md](../ffmpeg-source-offer.md).

## What a release produces

In `artifacts/`:

| File | |
|---|---|
| `captr-setup-<version>.exe` | The installer. |
| `SHA256SUMS.txt` | So a download can be verified. |
| `release.json` | Version, source commit, build timestamp, .NET version, the exact FFmpeg build id and hash, every file's hash, and whether it was signed. |
| `release-notes.md` | The notes that were published. |

All four are attached to the GitHub release.

`release.json` is the machine-readable answer to "what exactly is this build?", and
it matches what a running installation reports through `captr version`.

## Release notes

`new-release.ps1` generates them from the commit subjects since the previous tag —
accurate, in order, and quicker to edit down than to write from scratch. Override
with `-Notes "..."`, or pass `-Draft` to create the release as a draft and edit it
on GitHub before anyone sees it.

## After publishing

Two things the scripts cannot do for you:

1. **Install the release on a real machine and run
   `pwsh build/verify-install.ps1`.** It checks the binaries and their signatures,
   that the CLI responds and is on PATH, and then performs a real short recording
   and confirms a correctly-named, playable file reaches a destination.

2. **Work through [release verification](release-verification.md)** — the checks
   that need clean virtual machines, a real SharePoint tenant, and a long soak.
   They are listed there as a checklist precisely because they cannot be automated
   in this repository.

## Setting up the repository for the first time

```powershell
pwsh build/create-github-repo.ps1 -Name captr
```

Creates the repository (private by default — making it public later is one click,
un-publishing is not), adds it as `origin`, and pushes the current branch. Safe to
run twice; an existing repository or remote is reported and left alone.

Add `-Public` for a public repository, and `-Owner <org>` to create it under an
organisation.
