using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Captr.Core.Settings;

namespace Captr.Core.Transfers;

/// <summary>
/// Proves a SharePoint destination's details actually work, BEFORE a recording
/// depends on them: acquires a token with the tenant/client/secret, then asks Graph
/// for the drive the Drive ID names. Owns the translation of Graph's failure modes
/// into sentences a person can act on — "HTTP 400 invalidRequest" helps nobody;
/// "the Drive ID is not one Graph recognises" says what to fix.
/// </summary>
/// <remarks>
/// Used by the destination editor's "Test connection" button. Deliberately reads
/// nothing but the drive's own metadata: the test must not create, list, or touch
/// any file in the library.
/// </remarks>
public static class SharePointConnectionTest
{
    /// <summary>
    /// Runs the test and returns a one-sentence success message naming the library.
    /// Throws <see cref="TransferException"/> with a user-facing sentence on failure.
    /// </summary>
    /// <param name="destination">The destination as currently filled in.</param>
    /// <param name="typedSecret">The secret typed into the editor but not yet saved,
    /// or null to use the one stored in Windows Credential Manager.</param>
    /// <param name="graphHttp">Test seam: a client whose BaseAddress points at the
    /// mock server. Null means the real Graph endpoint.</param>
    public static async Task<string> RunAsync(
        DestinationSettings destination,
        byte[]? typedSecret,
        HttpClient? graphHttp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination.TenantId) || string.IsNullOrWhiteSpace(destination.ClientId))
        {
            throw new TransferException(FailureKind.Permanent,
                "Enter the Tenant ID and Client ID first — the test signs in with them.");
        }

        if (string.IsNullOrWhiteSpace(destination.SharePointDriveId))
        {
            throw new TransferException(FailureKind.Permanent,
                "Enter the Drive ID first — it names the document library the test looks for.");
        }

        HttpClient http = graphHttp ?? new HttpClient
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        try
        {
            string token;
            try
            {
                token = await new MsalTokenProvider(destination, typedSecret)
                    .GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Microsoft.Identity.Client.MsalException exception)
            {
                // MSAL's messages name the exact AADSTS error, which is what an
                // administrator searches for — pass them through, with a plain
                // sentence in front for everyone else.
                throw new TransferException(FailureKind.Permanent,
                    "Signing in failed — check the Tenant ID, Client ID, and client secret. " +
                    $"The identity service said: {exception.Message}");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"drives/{Uri.EscapeDataString(destination.SharePointDriveId)}?$select=name,driveType,webUrl");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return $"Connected. Recordings will upload to “{ReadDriveName(body)}”.";
            }

            throw new TransferException(FailureKind.Permanent, response.StatusCode switch
            {
                HttpStatusCode.BadRequest =>
                    "Graph did not recognise the Drive ID. Copy the id exactly from " +
                    "GET /sites/{site-id}/drives — it is the long value starting with “b!”.",
                HttpStatusCode.NotFound =>
                    "No drive with that Drive ID exists. Check it against GET /sites/{site-id}/drives.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Signed in, but the app is not allowed to see this library. Grant the app registration " +
                    "access to the site (Sites.Selected needs a per-site permission grant).",
                _ => $"Graph answered HTTP {(int)response.StatusCode}: {body}",
            });
        }
        finally
        {
            // Only a client we created here is ours to close.
            if (graphHttp is null)
            {
                http.Dispose();
            }
        }
    }

    /// <summary>The drive's display name, or a stand-in if the reply is not the JSON
    /// we expect — a malformed success is still a success.</summary>
    private static string ReadDriveName(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("name", out JsonElement name)
                ? name.GetString() ?? "the document library"
                : "the document library";
        }
        catch (JsonException)
        {
            return "the document library";
        }
    }
}
