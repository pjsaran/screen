using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Captr.Core.Transfers;

/// <summary>
/// Microsoft Graph resumable upload (SPEC §7): create an upload session, send
/// sequential chunks whose size is a multiple of 320 KiB, persist the confirmed
/// offset after every chunk, resume an interrupted session from the server's
/// <c>nextExpectedRanges</c>, and verify the returned size before declaring
/// success. Implemented over plain HttpClient with an injectable base address so
/// the resume tests run against a local mock server (see the folder README for why
/// not the Graph SDK).
/// </summary>
public sealed class GraphUploader
{
    /// <summary>Graph requires chunk sizes in multiples of 320 KiB; 8 × 320 KiB is
    /// the size Microsoft's own guidance recommends (SPEC §7).</summary>
    public const int ChunkBytes = 8 * 327_680;

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;

    /// <param name="http">Client whose BaseAddress points at Graph — or at the mock
    /// server in tests.</param>
    public GraphUploader(HttpClient http, IAccessTokenProvider tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    /// <summary>Creates a new upload session for the target path.</summary>
    /// <param name="driveId">The Graph drive id of the target document library. This
    /// must be a REAL id — Graph has no "default" drive alias under <c>/drives/</c>,
    /// and sending one produces the "invalid drive id" error this parameter fixed.</param>
    /// <param name="driveItemPath">Path of the file within that drive.</param>
    /// <returns>The session's upload URL — persisted to the queue so a crash
    /// resumes THIS session instead of creating a new one.</returns>
    public async Task<string> CreateSessionAsync(string driveId, string driveItemPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"drives/{Uri.EscapeDataString(driveId)}/root:/{Uri.EscapeDataString(driveItemPath).Replace("%2F", "/", StringComparison.Ordinal)}:/createUploadSession");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        request.Content = new StringContent("""{"item":{"@microsoft.graph.conflictBehavior":"rename"}}""",
            System.Text.Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowOnFailureAsync(response, cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        string uploadUrl = document.RootElement.TryGetProperty("uploadUrl", out JsonElement url) ? url.GetString() ?? "" : "";
        RequireSecureUploadUrl(uploadUrl);
        return uploadUrl;
    }

    /// <summary>
    /// True when the file is already at <paramref name="driveItemPath"/> with exactly
    /// <paramref name="expectedBytes"/> bytes - an upload that completed although its
    /// final confirmation never arrived (a dropped connection, or a crash between the
    /// last chunk and the queue recording it).
    /// </summary>
    /// <remarks>
    /// Asked before starting a replacement upload session. Without it, the new
    /// session's "rename on conflict" rule would land a second copy beside the first,
    /// as "name 1.mkv". Size is the test because it is what Graph's own completion
    /// reply is checked against (see <see cref="UploadAsync"/>).
    /// </remarks>
    public async Task<bool> AlreadyUploadedAsync(
        string driveId, string driveItemPath, long expectedBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"drives/{Uri.EscapeDataString(driveId)}/root:/{Uri.EscapeDataString(driveItemPath).Replace("%2F", "/", StringComparison.Ordinal)}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await ThrowOnFailureAsync(response, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("size", out JsonElement size)
            && size.ValueKind == JsonValueKind.Number
            && size.GetInt64() == expectedBytes;
    }

    /// <summary>
    /// The upload URL is a capability: whoever holds it can write to the library
    /// without a token. It must therefore only ever be sent somewhere trustworthy, and
    /// over TLS. Graph always returns https; a URL that is not came from somewhere
    /// else (a tampered queue database), and receives nothing. Plain http is allowed
    /// only to this machine's loopback, which is where the test server lives.
    /// </summary>
    internal static void RequireSecureUploadUrl(string uploadUrl)
    {
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new TransferException(FailureKind.Permanent,
                "The upload address for this transfer is not a secure Microsoft address, so nothing was sent. " +
                "Stop the transfer and send the recording again.");
        }
    }

    /// <summary>
    /// Uploads from the confirmed offset onward, reporting each newly confirmed
    /// offset through <paramref name="onChunkConfirmed"/> (persisted by the queue).
    /// Returns the size Graph reports for the completed item.
    /// </summary>
    public async Task<long> UploadAsync(
        string uploadUrl, string filePath, long confirmedOffset,
        Action<long> onChunkConfirmed, CancellationToken cancellationToken)
    {
        RequireSecureUploadUrl(uploadUrl);
        await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long totalBytes = file.Length;
        long offset = confirmedOffset;

        // An interrupted session: ask the server where it actually got to — its
        // answer, not our memory, is the truth (SPEC §7: resume, don't restart).
        if (offset > 0)
        {
            offset = await QueryNextExpectedOffsetAsync(uploadUrl, cancellationToken).ConfigureAwait(false) ?? offset;
        }

        byte[] buffer = new byte[ChunkBytes];
        while (offset < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.Position = offset;
            int chunkLength = (int)Math.Min(ChunkBytes, totalBytes - offset);
            await file.ReadExactlyAsync(buffer.AsMemory(0, chunkLength), cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            request.Content = new ByteArrayContent(buffer, 0, chunkLength);
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + chunkLength - 1, totalBytes);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                // Final chunk accepted: verify the size Graph reports (SPEC §7).
                using JsonDocument document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                long reportedSize = document.RootElement.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : -1;
                if (reportedSize != totalBytes)
                {
                    throw new TransferException(FailureKind.Transient,
                        $"Graph reports {reportedSize} bytes for the completed upload, expected {totalBytes} — retrying.");
                }

                onChunkConfirmed(totalBytes);
                return reportedSize;
            }

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                offset = ParseNextExpectedOffset(
                    await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) ?? offset + chunkLength;
                onChunkConfirmed(offset);
                continue;
            }

            ThrowIfSessionExpired(response);
            await ThrowOnFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        throw new TransferException(FailureKind.Transient, "Upload loop ended without a completion response — retrying.");
    }

    private async Task<long?> QueryNextExpectedOffsetAsync(string uploadUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(uploadUrl), cancellationToken).ConfigureAwait(false);
        ThrowIfSessionExpired(response);
        if (!response.IsSuccessStatusCode)
        {
            return null; // A passing hiccup: carry on from what the queue remembers.
        }

        return ParseNextExpectedOffset(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The upload URL is pre-authenticated, so 404/410 (and 401, which no bearer token
    /// can cure here) mean the SESSION is gone - Graph expires idle sessions. It used
    /// to be booked as an ordinary permanent refusal, and Retry re-used the same dead
    /// URL, so the transfer could never succeed.
    /// </summary>
    private static void ThrowIfSessionExpired(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Unauthorized)
        {
            throw new UploadSessionExpiredException((int)response.StatusCode);
        }
    }

    /// <summary>"12345-" or "12345-99999" → 12345.</summary>
    private static long? ParseNextExpectedOffset(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("nextExpectedRanges", out JsonElement ranges)
                && ranges.GetArrayLength() > 0
                && ranges[0].GetString() is { } first)
            {
                string start = first.Split('-')[0];
                return long.Parse(start, CultureInfo.InvariantCulture);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>Maps HTTP failures to SPEC §7's taxonomy, preserving the server's
    /// message verbatim for the manual-retry view.</summary>
    private static async Task ThrowOnFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string serverBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        FailureKind kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => FailureKind.AuthExpired,
            // Throttling, timeouts, and a locked item all clear up on their own.
            HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.Locked => FailureKind.Transient,
            >= HttpStatusCode.InternalServerError => FailureKind.Transient,
            _ => FailureKind.Permanent,
        };

        // Retry-After may be a number of seconds or an HTTP date; both are honoured.
        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is { } wait && wait > TimeSpan.Zero ? wait : null,
            _ => null,
        };

        throw new TransferException(kind,
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {serverBody}", retryAfter);
    }
}

/// <summary>A classified transfer failure carrying the server's verbatim message
/// (SPEC §7) and any server-supplied retry delay.</summary>
public sealed class TransferException(FailureKind kind, string serverMessage, TimeSpan? retryAfter = null)
    : Exception(serverMessage)
{
    public FailureKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The upload session behind an upload URL no longer exists; the upload must
/// start a new session (after checking the file did not in fact already arrive).</summary>
public sealed class UploadSessionExpiredException(int statusCode) : Exception(
    $"The upload session has expired (HTTP {statusCode}); a new one is needed.");

/// <summary>Supplies bearer tokens for Graph. The production implementation uses
/// MSAL with the DPAPI token cache; tests use a fake.</summary>
public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
