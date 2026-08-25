using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Captr.Core.Ipc;

/// <summary>
/// The wire format between UI/CLI and the recording host: framing, the envelope,
/// and serialization. Owns compatibility — the protocol version lives here, and
/// every shape change must bump it so a stale client fails clearly (SPEC §4).
/// </summary>
public static class IpcProtocol
{
    /// <summary>
    /// Bump on ANY breaking change to messages or framing. History:
    /// 1 — initial protocol.
    /// 2 — StatusResponse gained live coverage (gap count + covered fraction);
    ///     resend and clip messages added.
    /// 3 — clip removed; StartRequest's single quality preset split into a speed
    ///     preset and a quality level; StatusResponse reports both.
    /// 4 — "delivery" became "transfer" throughout (kinds renamed); transfers gained
    ///     a cancel verb; RecordingSummary carries its finalised output file names.
    /// </summary>
    public const int Version = 4;

    /// <summary>Envelopes larger than this are rejected — no legitimate message is
    /// near it, and a corrupt length prefix must not allocate gigabytes.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The pipe name for the current user. The SID hash keeps one user's
    /// host separate from another's on shared machines; the security descriptor
    /// (see IpcServer) does the actual enforcement.</summary>
    /// <param name="instanceSuffix">
    /// Test seam. Production passes nothing, so every UI and CLI on this account
    /// finds the one real host. Tests pass a unique value so their in-process
    /// server gets a PRIVATE pipe — without it, a Captr host installed on the
    /// developer's machine shadows the test server and the tests silently talk to
    /// the real product instead (which is exactly how this seam came to exist).
    /// </param>
    public static string PipeName(string? instanceSuffix = null)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? "unknown";
        // A short stable hash keeps the name well under the pipe-name length cap.
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid));
        string name = "captr-host-" + Convert.ToHexStringLower(hash)[..16];
        return instanceSuffix is null ? name : name + "-" + instanceSuffix;
    }

    /// <summary>Writes one envelope: 4-byte little-endian length + UTF-8 JSON.</summary>
    public static async Task WriteAsync(Stream stream, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        byte[] lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one envelope, or null at a clean end of stream.</summary>
    public static async Task<IpcEnvelope?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthPrefix = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, lengthPrefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException($"IPC frame length {length} is outside the valid range — corrupt stream.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IpcEnvelope>(payload, SerializerOptions);
    }

    /// <summary>Typed payload extraction.</summary>
    public static T? PayloadAs<T>(this IpcEnvelope envelope) =>
        envelope.Payload is { } payload ? payload.Deserialize<T>(SerializerOptions) : default;

    /// <summary>Builds an envelope around a typed payload.</summary>
    public static IpcEnvelope Envelope(string kind, object? payload = null) => new()
    {
        V = Version,
        Kind = kind,
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, SerializerOptions),
    };

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException("IPC stream ended mid-frame.");
            }

            offset += read;
        }

        return true;
    }
}

/// <summary>One message on the wire.</summary>
public sealed record IpcEnvelope
{
    /// <summary>Protocol version of the sender.</summary>
    public required int V { get; init; }

    /// <summary>Message kind, e.g. "hello", "start", "status", "error".</summary>
    public required string Kind { get; init; }

    public JsonElement? Payload { get; init; }
}

// ---- Message kinds and typed payloads ------------------------------------------

/// <summary>Message kind names. Requests and their responses share a kind.</summary>
public static class IpcKinds
{
    public const string Hello = "hello";
    public const string Error = "error";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Status = "status";
    public const string ListRecordings = "list-recordings";
    public const string Verify = "verify";
    public const string Recover = "recover";
    public const string ListTransfers = "list-transfers";
    public const string RetryTransfer = "retry-transfer";
    public const string CancelTransfer = "cancel-transfer";
    public const string Resend = "resend";
    public const string SetSettings = "set-settings";
}

public sealed record HelloRequest(int ProtocolVersion, string ClientVersion);

public sealed record HelloResponse(bool Accepted, int HostProtocolVersion, string HostVersion, string? RefusalReason);

/// <summary>Any failure, including the unknown-kind reply (SPEC §14: unknown kinds
/// are answered, not fatal).</summary>
public sealed record ErrorResponse(string Message);

/// <summary>Start a recording. Every field is a per-session override of the saved
/// settings; null means "use what is saved".</summary>
public sealed record StartRequest(int? FrameRate, string? Quality, string? SpeedPreset, string? Label);

public sealed record StartResponse(bool AlreadyRecording, Guid SessionId, string Message);

public sealed record StopResponse(bool WasRecording, string State);

public sealed record StateResponse(string State, string Message);

/// <summary>
/// What the recorder is doing right now. <c>GapCount</c> and <c>Coverage</c> are
/// the live coverage SPEC §9 requires the status view to show WHILE recording —
/// holes are visible as they happen, not only after finalisation.
/// </summary>
public sealed record StatusResponse(
    string State,
    Guid? SessionId,
    DateTimeOffset? StartedUtc,
    TimeSpan? Elapsed,
    string? Encoder,
    int? FrameRate,
    TimeSpan? EncodedTime,
    string? WorkingFolder,
    double? DiskMinutesRemaining,
    int GapCount = 0,
    double Coverage = 1.0,
    string? Quality = null,
    string? SpeedPreset = null);

/// <summary>One past recording, as every list of them sees it.</summary>
/// <param name="OutputFiles">The finalised video files' names, from the integrity
/// record. Empty until the session is finalised. Carried here so callers can offer
/// "send again" only when there is actually something to send.</param>
public sealed record RecordingSummary(
    string Folder,
    DateTimeOffset StartedUtc,
    TimeSpan RecordedSpan,
    long TotalBytes,
    int GapCount,
    bool Finalized,
    IReadOnlyList<string>? OutputFiles = null);

public sealed record ListRecordingsResponse(IReadOnlyList<RecordingSummary> Recordings);

public sealed record VerifyRequest(string Folder);

public sealed record VerifyResponse(bool Intact, IReadOnlyList<string> Problems);

public sealed record RecoverResponse(IReadOnlyList<RecoverySummary> Recovered);

public sealed record RecoverySummary(string Folder, bool Succeeded, string? FailureReason, TimeSpan RecoveredDuration);

public sealed record TransferSummary(
    long Id,
    string OutputPath,
    string DestinationName,
    string State,
    int Attempts,
    DateTimeOffset? NextAttemptUtc,
    string? LastError);

/// <summary>Stop a transfer that is queued, running, or backing off. It stays in
/// the list and can be retried by hand; nothing local is touched.</summary>
public sealed record CancelTransferRequest(long Id);

public sealed record ListTransfersResponse(IReadOnlyList<TransferSummary> Transfers);

public sealed record RetryTransferRequest(long Id);

/// <summary>Re-send a finished recording's outputs to every enabled destination
/// (SPEC §9's "re-sending to a destination").</summary>
public sealed record ResendRequest(string Folder);

public sealed record ResendResponse(int Queued, string Message);

/// <summary>Save settings THROUGH the host, so the capture/quality lock of SPEC §8
/// can be enforced against the live session.</summary>
public sealed record SetSettingsRequest(Settings.CaptrSettings Settings);

public sealed record SetSettingsResponse(bool Applied, bool DegradedLiveRecording, string Message);
