# Secrets

The ONLY folder that touches secret material (SPEC §7: "confine all secret handling
to a single module; everything else passes an opaque reference"). If code outside
this folder holds a secret as a string, that is a defect by definition — the
BannedSymbols list and the redaction tests exist to catch it.

- **CredentialVault** — stores secrets in Windows Credential Manager, additionally
  DPAPI-wrapped under the current user with a fixed application entropy, so a blob
  exfiltrated from one machine/account is useless on another (SPEC §7). The API
  never returns a secret: callers pass a function that receives the bytes, and the
  bytes are zeroed in a `finally` block the moment it returns. Reading back "what
  is stored" yields only the name and the storage timestamp — the UI is write-only
  by construction.
- **SecretRedaction** — the Serilog policy registered at logger construction
  (never at call sites, SPEC §12) that scrubs any property whose name suggests a
  secret. Discipline is not the mechanism; this is.

Never write a secret to: settings, the journal, logs, IPC responses, diagnostics
bundles, or exception messages. The diagnostics-bundle test plants a secret and
searches the output to prove the whole chain holds (SPEC §9).
