# SharePoint destination setup

Captr uploads finished recordings into a SharePoint document library through the
Microsoft Graph API. That needs an **app registration** in your tenant — a one-time
job, usually for whoever administers Microsoft 365.

You will end up with four things to type into Captr: a **tenant ID**, a **client
ID**, a **client secret**, and the document library's **drive ID**.

## 1. Create the app registration

Entra admin centre → **App registrations** → **New registration**.

1. Name it something recognisable — `Captr Recorder`. Single tenant.
2. **API permissions** → Microsoft Graph → **Application permissions**:
   - `Sites.Selected` is the right choice. It grants nothing on its own; your
     SharePoint administrator then gives the app write permission on **one specific
     site**, which is as narrow as this can be made.
   - `Files.ReadWrite.All` also works but grants access to everything. Avoid it if
     you can.

   Grant admin consent for whichever you choose.
3. If you used `Sites.Selected`, have your SharePoint administrator assign the app
   *write* permission on the target site (a one-off call to the Graph
   `sites/{site-id}/permissions` endpoint).
4. **Certificates & secrets** → **New client secret**. Copy the **Value** — not the
   Secret ID — immediately. It is shown once.

From the registration's Overview page, note the **Directory (tenant) ID** and the
**Application (client) ID**.

## 2. Find the drive ID

Uploads are addressed to a specific **drive** — Graph's name for one document
library. A site can hold several, so Captr asks for the id of the exact one.

Using [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer)
(or any signed-in Graph client):

1. `GET https://graph.microsoft.com/v1.0/sites/contoso.sharepoint.com:/sites/Recordings`
   — note the `id` in the reply (the site id).
2. `GET https://graph.microsoft.com/v1.0/sites/{site-id}/drives` — each library is
   listed with its `name` and `id`.
3. Copy the `id` of the library you want. It is a long value starting with `b!`.

## 3. Add the destination in Captr

**Settings → Destinations → Add destination → SharePoint.**

| Field | What to put in it |
|---|---|
| Name | Your label, e.g. `SharePoint archive`. |
| Site URL | `https://contoso.sharepoint.com/sites/Recordings` |
| Drive ID | the `b!…` value from step 2 |
| Folder in the library | `Captr`, or with a [token](naming-patterns.md) such as `Captr/{date:yyyy-MM}`. A path **inside** the library the Drive ID names, starting at its top level — not a drive letter, not a `\\server` path, and no `..`. |
| Tenant ID | from the registration |
| Client ID | from the registration |
| Client secret | the **Value** you copied |

Press **Test connection** before saving: it signs in with exactly what is in the
boxes — the secret you have just typed, not a sign-in remembered from earlier — and
asks Graph for the library the Drive ID names. Success shows the library's name;
failure says which field to fix. Nothing is uploaded and nothing is stored by the
test.

Save. The secret goes straight into Windows Credential Manager and **never** into
`settings.json`. Captr cannot read it back afterwards — reopening the destination
tells you a secret is stored and offers to replace it, nothing more.

That is by design: a write-only secret cannot be leaked by a settings export, a
support bundle, a log file, or a screenshot.

### Doing it from a script instead

```bat
type secret.txt | captr auth set-secret "Captr:SharePoint archive"
```

The name is `Captr:` followed by the destination's name — that is the entry Captr
looks for. Secrets are never accepted as command-line arguments, because argument
lists are visible to every process on the machine.

## 4. Rotating the secret

When the secret expires or is rotated:

1. Create the new secret in the app registration.
2. **Settings → Destinations →** the destination **→ Edit**, type the new secret,
   Save. It replaces the old one.
3. **Transfers → Retry all failed**, or `captr transfers retry <id>`, to re-arm
   anything that stopped while the old secret was dead.

Transfers that failed on authentication show **SIGN-IN NEEDED** and resume on the
next pass once a working credential exists. That covers both a secret Microsoft
sign-in refuses (mistyped, expired, deleted) and an access token SharePoint
rejects; in either case nothing is uploaded until new credentials are saved.

## What to expect operationally

- Large files use a Graph **upload session**: sequential chunks of 2,621,440 bytes
  (8 × 320 KiB, the multiple Graph requires), with the confirmed position recorded
  after **every** chunk. A crash, a reboot, or a dropped connection resumes from
  where the server says it got to — never from zero.
- An upload session SharePoint has expired in the meantime is replaced by a new
  one — after checking whether the file already arrived, so a lost final reply
  never produces a second copy. Uploads only ever go to `https` addresses.
- Throttling (429), timeouts (408), a locked item (423), and server errors (5xx)
  retry automatically with backoff, honouring the server's own `Retry-After` when it
  sends one.
- Permission, quota, and policy errors stop and wait for a person, showing the
  server's exact words. Those are not problems that retrying solves. With
  `Sites.Selected`, a site the app was never granted is one of these: the transfer
  shows **REFUSED** with SharePoint's reason (typically `accessDenied`) until the
  site is granted and the transfer retried.
- A finished upload is checked by comparing the size SharePoint reports with the
  local file's.
- Captr never overwrites a file in the library; a name that is taken gets a suffix.
- A failed upload can never endanger the local recording.

## Renaming or removing the destination

Both are handled for you:

- **Renaming** a destination moves its stored secret to follow the new name.
- **Removing** a destination deletes its stored secret from Windows Credential
  Manager.

You do not need to tidy up Credential Manager by hand.
