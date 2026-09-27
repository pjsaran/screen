# Releasing

## The short version

```powershell
pwsh build/new-release.ps1 -Bump minor
```

That checks that signing is configured, bumps the version, commits the bump, builds
and tests everything, signs every shipped file, produces the installer, verifies
every signature and the artefacts against their recorded hashes, tags the commit,
pushes, and creates the GitHub release with the installer attached.

Nothing is tagged or published until every earlier step has passed.

**A release must be signed.** `new-release.ps1` refuses to start — before the
version bump, before the build — unless a [signing method](#code-signing) is
configured and `CAPTR_SIGN_PUBLISHER` is set, and it refuses to tag if
`release.json` does not record verified signatures. `-AllowUnsigned` switches both
checks off, for a build nobody else will install.

## Rehearse it first

```powershell
pwsh build/new-release.ps1 -SkipPublish
```

Everything except tagging, pushing, and GitHub. Use this the first time, and any
time you have changed the packaging. A rehearsal signs when a method is configured
and warns when one is not, rather than refusing.

## The scripts

| Script | Does |
|---|---|
| `build/set-version.ps1` | Sets the version in `Version.props`. Nothing else. |
| `build/build.ps1` | Format, licence gate, build, test, publish (signing the published files), installer. |
| `build/make-installer.ps1` | Inno Setup compile (signing the installer and uninstaller), signature verification, symbols zip, `SHA256SUMS.txt`, `release.json`. |
| `build/Sign-Artifacts.ps1` | Authenticode signing of everything Captr ships, by any of three methods. `-DryRun` shows the plan with no certificate. |
| `build/Verify-Signatures.ps1` | Fails on any shipped file that is unsigned, untimestamped, timestamped the legacy way, or signed by the wrong publisher. |
| `build/verify-install.ps1` | Post-install verification on a real machine. |
| `build/new-release.ps1` | All of the above, in the right order, plus tag and publish. |
| `build/create-github-repo.ps1` | One-time: create the GitHub repository and push. |

`build/sign.ps1` is kept only so older habits keep working: it forwards to
`Sign-Artifacts.ps1`. Use `Sign-Artifacts.ps1` directly.

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

Versions are plain `MAJOR.MINOR.PATCH`. A pre-release suffix such as `0.2.0-beta.1`
is refused up front: Windows file versions and Inno Setup's version resource must be
numeric, and the next build would otherwise fail with CS7034.

Semantic versioning: **major** for a break, **minor** for a feature, **patch** for a
fix. Two things in particular are breaking changes for a user:

- a **settings schema** bump without a migration (there must always be a migration
  — see `src/Captr.Core/Settings/Migrations/`);
- an **IPC protocol** bump (`IpcProtocol.Version`), which makes an old CLI refuse to
  talk to a new host. That is intended behaviour, but it is a break.

`new-release.ps1` refuses a version whose tag already exists — in this clone **or on
`origin`**, so a tag pushed from another machine stops the release before the build
rather than at the push.

## Code signing

### What signing does, and does not, buy

A valid Authenticode signature makes Windows show Captr's publisher name instead of
"Unknown publisher" in the UAC prompt and the file's Properties, lets Smart App
Control and application-control policies (which block unsigned code) run it, and
proves the files have not been changed since they were signed.

It does **not** buy SmartScreen's trust on the first day. SmartScreen reputation
builds up over time, per certificate and per file, as people download and run the
release. A new OV certificate starts with none, so early downloads may still show
"Windows protected your PC" until that reputation accrues. That is expected, not a
signing fault.

### What gets signed

`Sign-Artifacts.ps1` looks at every `.exe` and `.dll` it is given:

| File | What happens |
|---|---|
| Captr's own binaries (`Captr.*`, `captr.exe`) | Always signed with Captr's certificate. |
| The bundled `ffmpeg.exe` and `ffprobe.exe` | Signed with Captr's certificate, but **only after** each one's pinned digest is verified against the `capabilities.json` beside it (recorded by `fetch-ffmpeg.ps1` straight after the pinned archive's SHA-256 was checked). A mismatch stops the run: signing something we cannot prove is the pinned build would put Captr's name on it. The digest ignores the signature, so the application's own check of FFmpeg still matches after signing. |
| Third-party files already signed and timestamped by a trusted publisher (the .NET runtime, WPF — the list is in `build/signing.lock.json`) | Left exactly as they are. |
| Every other third-party DLL (unsigned ones) | Signed with Captr's certificate, so nothing shipped is unsigned. |
| The installer, and the uninstaller inside it | Signed by Inno Setup as it builds them (`SignTool` + `SignedUninstaller` in `captr.iss`, calling `Sign-Artifacts.ps1`), so `unins000.exe` does not show as an unknown publisher either. |

`build.ps1` signs `publish/` as part of its publish step, and `make-installer.ps1`
signs the installer and uninstaller as Inno compiles them.

Every signature is SHA-256 and gets an **RFC 3161** timestamp, so it stays valid
after the certificate expires. Timestamp servers come from
`build/signing.lock.json` and are tried in order, twice each, before the run fails;
`CAPTR_SIGN_TIMESTAMP_URL` puts a server of your choice first. An untimestamped
signature dies with its certificate, which is exactly what the timestamp prevents,
so a failure to timestamp fails the build.

`signtool` itself comes from the `Microsoft.Windows.SDK.BuildTools` NuGet package,
fetched into `tools/signing` and checked against the SHA-512 nuget.org publishes for
it (pinned in `signing.lock.json`). No Windows SDK install is needed.

### Choosing a method

Credentials come **only** from environment variables (which is what CI secrets
become) or the Windows certificate store — never from a file in the repository.

| Method | Set | Notes |
|---|---|---|
| **Pfx** — a certificate file | `CAPTR_SIGN_PFX` (a path) **or** `CAPTR_SIGN_PFX_BASE64` (the file's content, base64 — the CI form), plus `CAPTR_SIGN_PFX_PASSWORD` | The certificate is loaded inside the signing process and never written to disk; the password never appears on a command line. The RFC 3161 timestamp is then added by signtool, which needs no key to do it. |
| **CertStore** — a certificate in the Windows store | `CAPTR_SIGN_CERT_THUMBPRINT`, and optionally `CAPTR_SIGN_CERT_STORE` = `CurrentUser` (default) or `LocalMachine` | signtool selects it by thumbprint from the `My` store. Works with hardware tokens and EV certificates. |
| **ArtifactSigning** — Azure Artifact Signing (formerly Trusted Signing) | `CAPTR_ARTIFACT_SIGNING_ENDPOINT`, `CAPTR_ARTIFACT_SIGNING_ACCOUNT`, `CAPTR_ARTIFACT_SIGNING_PROFILE`, and an Azure identity: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` (or a CI workload identity) | Nothing secret is stored locally. The pinned Artifact Signing client library is fetched only when this method is used, and its own timestamp servers are used. |

When more than one is configured, the first in that order wins. `CAPTR_SIGN_METHOD`
(`Pfx`, `CertStore`, or `ArtifactSigning`) forces one. Selecting a method with a
variable missing fails at once, naming the missing variable, rather than half
working.

**`CAPTR_SIGN_PUBLISHER`** is not a credential: it is the common name (CN) on
Captr's certificate — for example `Contoso Ltd` — and verification checks every one
of Captr's files against it. `make-installer.ps1` refuses to build a signed
installer without it.

### Signing on your own machine

```powershell
# See exactly what would happen. No certificate, no network, nothing changed -
# and the FFmpeg digests are still verified.
pwsh build/Sign-Artifacts.ps1 -Path publish -DryRun

# Then, for example with a certificate in your store:
$env:CAPTR_SIGN_CERT_THUMBPRINT = '<thumbprint>'
$env:CAPTR_SIGN_PUBLISHER       = 'Contoso Ltd'
pwsh build/build.ps1 -Installer -RequireSigning
```

With nothing configured, signing prints a prominent warning and the plan, and the
build **succeeds** — a contributor without a certificate must always be able to
build and test the real installer. `-RequireSigning` on `build.ps1` and
`make-installer.ps1` (and `-Require` on `Sign-Artifacts.ps1`) turns that into a
failure; `new-release.ps1` passes it.

For a rehearsal of the signed path, a self-signed code-signing certificate is
enough. Verification requires a chain Windows trusts, so either add the
certificate to **Trusted Root Certification Authorities** for your account for the
duration, or run `Verify-Signatures.ps1 -AllowUntrustedRoot` yourself, which accepts
a chain whose only fault is the untrusted root. `verify-install.ps1
-SkipSignatureCheck` covers an unsigned install.

### Verification

`Verify-Signatures.ps1` checks every Windows executable and DLL under the paths it
is given and prints one line per problem:

| Problem | Meaning |
|---|---|
| `UNSIGNED` | No signature at all. |
| `INVALID` | Windows does not accept the signature (a broken chain, a modified file). |
| `NOT TIMESTAMPED` | No timestamp: the signature would die with the certificate. |
| `NOT RFC 3161` | A legacy Authenticode timestamp (what PowerShell's own signing adds). |
| `WRONG SIGNER` | One of Captr's files (`ownFilePatterns` in `signing.lock.json`: our binaries, FFmpeg, the installer and uninstaller) not signed by `CAPTR_SIGN_PUBLISHER`, or any other file signed by someone who is neither that publisher nor on the trusted list. |

It exits non-zero if there are any. `make-installer.ps1` runs it over `publish/` and
the installer whenever a signing method is configured, and writes the result to
`artifacts/signature-report.json`. `release.json` then records `"signed": true` and
`"signedBy"` — which means **verified**, not merely "a certificate was configured".
An unsigned build skips verification loudly and records `"signed": false`.

### In CI

`.github/workflows/ci.yml` builds the installer on every push and pull request.
Signing uses the Pfx method, from:

| Name | Kind | Holds |
|---|---|---|
| `CAPTR_SIGN_PFX_BASE64` | repository **secret** | The `.pfx` file, base64-encoded (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('captr.pfx'))`). |
| `CAPTR_SIGN_PFX_PASSWORD` | repository **secret** | Its password. |
| `CAPTR_SIGN_PUBLISHER` | repository **variable** | The certificate's CN. |

Where the secrets exist, the packaging step signs and verifies, and any unsigned,
untimestamped, or wrongly signed file fails the job. Pull requests and forks have no
secrets and build unsigned, with the warning — which must succeed. A signing dry run
(`Sign-Artifacts.ps1 -Path publish -DryRun`) runs on every build either way, proving
the plan and the FFmpeg digests.

### When signing fails

| Message | Cause and fix |
|---|---|
| `No code-signing method is configured, and a release must be signed` / `Signing is required but no signing method is configured` | None of the method variables is set in this shell. Set one method's variables, or pass `-AllowUnsigned` for a build nobody else installs. |
| `CAPTR_SIGN_PUBLISHER is not set` / `Signing is configured but CAPTR_SIGN_PUBLISHER is not` | Set it to the certificate subject's CN. |
| `Signing method '…' is selected but these environment variables are not set: …` | Exactly those are missing. |
| `CAPTR_SIGN_PFX points to a missing file` / `The PFX holds no private key.` | The wrong file, or a `.cer` exported without its key. Export the certificate **with** its private key as `.pfx`. |
| `Refusing to sign …\ffmpeg.exe: it is not the pinned FFmpeg build` (or `…records no digest for it`) | `publish/` holds an FFmpeg that is not the one `fetch-ffmpeg.ps1` verified. Run `pwsh build/fetch-ffmpeg.ps1`, then publish again. Never sign around it. |
| signtool: `No certificates were found that met all the given criteria.` | CertStore: the thumbprint is wrong (copy it without spaces or hidden characters), the certificate is in the other store (`CAPTR_SIGN_CERT_STORE=LocalMachine`), or it has no private key on this machine (a token not plugged in). |
| `Timestamp server … failed; trying the next one.` then `No timestamp server could timestamp …` | Every server was unreachable — usually a proxy or firewall blocking plain `http` to the timestamp authorities. Allow them, or set `CAPTR_SIGN_TIMESTAMP_URL` to one you can reach. Re-run; signing is idempotent. |
| `WRONG SIGNER … signed by 'X'; expected 'Y'` | `CAPTR_SIGN_PUBLISHER` does not match the certificate's CN exactly, or a third-party file is signed by a publisher not on the trusted list. |
| `INVALID … root certificate which is not trusted` | A self-signed rehearsal certificate: see [signing on your own machine](#signing-on-your-own-machine). |
| `NOT RFC 3161` | The file was timestamped by something other than `Sign-Artifacts.ps1`. Sign it again through the script. |
| ArtifactSigning: signtool fails with an authentication or 403 error | The Azure identity in `AZURE_*` cannot sign with that account and certificate profile: check the tenant, client, and secret, and that the identity has the signer role on the certificate profile. |

> Signing is the one documented modification to the bundled FFmpeg binaries. An
> unsigned screen-capturing executable with a well-known name is a textbook
> endpoint-protection detection, so the executables are signed at release time. This
> is recorded in [ffmpeg-source-offer.md](../ffmpeg-source-offer.md).

## What a release produces

In `artifacts/`:

| File | |
|---|---|
| `captr-setup-<version>.exe` | The installer. |
| `SHA256SUMS.txt` | SHA-256 of the installer and the symbols zip, so a download can be verified. |
| `release.json` | Version, source commit, the commit's time, the .NET SDK and bundled runtime versions, the exact FFmpeg build id and hash, the hash of each file above, and whether every shipped file was verified as signed (and by whom). |
| `signature-report.json` | What `Verify-Signatures.ps1` checked and found (signed builds only). |
| `captr-symbols-<version>.zip` | The `.pdb` files for Captr's own assemblies. |
| `release-notes.md` | The notes that were published. |

`new-release.ps1` attaches **three** of these to the GitHub release: the installer,
`SHA256SUMS.txt`, and `release.json`.

**Symbols are archived, never shipped.** The build moves Captr's PDBs out of
`publish/` into `artifacts/symbols` (and drops the XML documentation files), so
nothing debugging-related reaches a user's machine; the installer also excludes
`*.pdb` in case a stale `publish/` holds one. Keep `captr-symbols-<version>.zip` with
the release — it is what turns a crash report's stack trace into file and line
numbers for that exact build — but it is not uploaded to GitHub by the script.

The same commit builds the same bytes: publishing uses `ContinuousIntegrationBuild`
(no local paths in the binaries), and neither the shipped `capabilities.json` nor
`release.json` carries the time of the build — `release.json` records the commit's
time instead.

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
   and confirms a correctly-named, playable file reaches a destination. It works in
   a data root of its own and configures Captr through `captr settings
   export`/`import`, so the machine's real settings are left byte-for-byte as they
   were.

2. **Work through [release verification](release-verification.md)** — the checks
   that need a real certificate, real hardware, a real SharePoint tenant, and a
   person looking at the screen. They are listed there as a checklist precisely
   because they cannot be automated in this repository.

## Setting up the repository for the first time

```powershell
pwsh build/create-github-repo.ps1 -Name captr
```

Creates the repository (private by default — making it public later is one click,
un-publishing is not), adds it as `origin`, and pushes the current branch. Safe to
run twice; an existing repository or remote is reported and left alone.

Add `-Public` for a public repository, and `-Owner <org>` to create it under an
organisation.
