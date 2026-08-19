# SharePoint delivery: Microsoft Graph setup

Captr uploads finished recordings to a SharePoint document library via Microsoft
Graph resumable upload sessions. Two credential modes are supported (SPEC §7):

- **Application-only (client credential)** — a client secret or certificate on an
  app registration. Right for unattended machines and scheduled recording:
  nothing interactive, tokens acquired headlessly.
- **Delegated (interactive sign-in)** — a user signs in once in the UI; tokens are
  cached (DPAPI-protected) and refreshed silently.

## 1. App registration (once per tenant)

In Entra admin center → App registrations → New registration:

1. Name: e.g. `Captr Recorder`. Single tenant.
2. **API permissions** → Microsoft Graph:
   - Application-only mode: **Application permission** `Sites.Selected`
     (recommended — grants nothing until a site is assigned) or `Files.ReadWrite.All`
     (broad; avoid if possible). Grant admin consent.
   - Delegated mode: **Delegated permission** `Files.ReadWrite.All` (+ admin
     consent if your tenant requires it).
3. Application-only with `Sites.Selected`: assign the app *write* permission on
   the specific site via the Graph `sites/{site-id}/permissions` endpoint (your
   SharePoint admin does this once).
4. Certificates & secrets → New client secret. Copy the **value** immediately.

Record from the registration: **Tenant ID** and **Application (client) ID**.

## 2. Configure the machine

Store the secret — never in a file, never as a command-line argument:

```bat
captr auth set-secret sp-archive
```

(paste at the masked prompt, or pipe it in). Then configure the destination in
`captr settings get`-style JSON or the Settings page:

```jsonc
"destinations": [
  {
    "name": "sharepoint-archive",
    "kind": "sharePoint",
    "enabled": true,
    "sharePointSiteUrl": "https://contoso.sharepoint.com/sites/trading",
    "sharePointFolder": "Recordings/Desk4",
    "tenantId": "<tenant-guid>",
    "clientId": "<app-client-guid>",
    "credentialName": "sp-archive"
  }
]
```

`credentialName` points at the Credential Manager entry — the settings file never
contains the secret, and settings export says so explicitly.

## 3. Rotation

```bat
captr auth set-secret sp-archive      # overwrite with the new secret value
captr delivery retry <id>             # re-arm anything that paused on auth
```

Deliveries that failed while the old secret was expired sit in `paused-auth`; they
resume automatically on the next queue pass after a working credential exists.

## How the upload behaves (what to expect operationally)

- Large files use a Graph **upload session**: sequential chunks of 2,621,440 bytes
  (8 × 320 KiB — the multiple Graph requires), with the confirmed offset persisted
  after **every** chunk. A crash, reboot, or network loss resumes from the
  server's `nextExpectedRanges`, never from zero.
- Throttling (429) and server errors (5xx) retry automatically with exponential
  backoff, honouring `Retry-After`. Permission/quota/policy errors park in
  `manual-retry` with the server's own message shown verbatim
  (`captr delivery list`).
- A delivery failure can never endanger the local recording (SPEC §13).
