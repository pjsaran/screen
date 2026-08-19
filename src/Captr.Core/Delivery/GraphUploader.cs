using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Captr.Core.Delivery;

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
    /// <returns>The session's upload URL — persisted to the queue so a crash
    /// resumes THIS session instead of creating a new one.</returns>
    public async Task<string> CreateSessionAsync(string driveItemPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"drives/default/root:/{Uri.EscapeDataString(driveItemPath).Replace("%2F", "/", StringComparison.Ordinal)}:/createUploadSession");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        request.Content = new StringContent("""{"item":{"@microsoft.graph.conflictBehavior":"rename"}}""",
            System.Text.Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowOnFailureAsync(response, cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.GetProperty("uploadUrl").GetString()
            ?? throw new DeliveryException(FailureKind.Permanent, "Graph's createUploadSession reply had no uploadUrl.");
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
                    throw new DeliveryException(FailureKind.Transient,
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

            await ThrowOnFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        throw new DeliveryException(FailureKind.Transient, "Upload loop ended without a completion response — retrying.");
    }

    private async Task<long?> QueryNextExpectedOffsetAsync(string uploadUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(uploadUrl), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null; // The session may have expired; the caller restarts cleanly.
        }

        return ParseNextExpectedOffset(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
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
            HttpStatusCode.TooManyRequests => FailureKind.Transient,
            >= HttpStatusCode.InternalServerError => FailureKind.Transient,
            _ => FailureKind.Permanent,
        };
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;

        throw new DeliveryException(kind,
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {serverBody}", retryAfter);
    }
}

/// <summary>A classified delivery failure carrying the server's verbatim message
/// (SPEC §7) and any server-supplied retry delay.</summary>
public sealed class DeliveryException(FailureKind kind, string serverMessage, TimeSpan? retryAfter = null)
    : Exception(serverMessage)
{
    public FailureKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Supplies bearer tokens for Graph. The production implementation uses
/// MSAL with the DPAPI token cache; tests use a fake.</summary>
public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
