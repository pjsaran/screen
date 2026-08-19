using System.Diagnostics;
using System.IO.Pipes;

namespace Captr.Core.Ipc;

/// <summary>
/// The UI/CLI end of the pipe: connect, shake hands, exchange request/response.
/// Owns "the host starts on demand" (SPEC §4): when no host answers,
/// <see cref="ConnectAsync"/> can spawn <c>Captr.exe --host</c> detached and retry.
/// If this class fails, nothing can talk to the host — so every failure mode maps
/// to a clear exception the CLI turns into a documented exit code.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HostStartupTimeout = TimeSpan.FromSeconds(60);

    private readonly NamedPipeClientStream _pipe;
    private readonly string _clientVersion;

    private IpcClient(NamedPipeClientStream pipe, string clientVersion)
    {
        _pipe = pipe;
        _clientVersion = clientVersion;
    }

    /// <summary>
    /// Connects to a running host, optionally starting one when none answers.
    /// </summary>
    /// <param name="startHostIfNeeded">True for operations that should summon a
    /// host (start, recover); false for pure queries where "no host" simply means
    /// "idle" (status when nothing is recording).</param>
    /// <param name="hostExecutablePath">Path to Captr.exe; defaults to the one
    /// beside the current executable.</param>
    public static async Task<IpcClient?> ConnectAsync(
        string clientVersion, bool startHostIfNeeded, string? hostExecutablePath, CancellationToken cancellationToken)
    {
        IpcClient? client = await TryConnectOnceAsync(clientVersion, cancellationToken).ConfigureAwait(false);
        if (client is not null || !startHostIfNeeded)
        {
            return client;
        }

        StartDetachedHost(hostExecutablePath);

        // The host takes a moment to open its pipe (recovery scan runs first).
        DateTimeOffset deadline = DateTimeOffset.UtcNow + HostStartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            client = await TryConnectOnceAsync(clientVersion, cancellationToken).ConfigureAwait(false);
            if (client is not null)
            {
                return client;
            }
        }

        throw new HostUnreachableException(
            "A recording host was started but did not begin answering within " +
            $"{HostStartupTimeout.TotalSeconds:F0} seconds. Check the host log in %LOCALAPPDATA%\\Captr\\logs.");
    }

    private static async Task<IpcClient?> TryConnectOnceAsync(string clientVersion, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        var client = new IpcClient(pipe, clientVersion);
        try
        {
            // A handshake that hangs (e.g. a half-dead host) must fail, not block
            // the CLI forever.
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.HandshakeAsync(handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        return client;
    }

    private static void StartDetachedHost(string? hostExecutablePath)
    {
        string exePath = hostExecutablePath
            ?? Path.Combine(AppContext.BaseDirectory, "Captr.exe");
        if (!File.Exists(exePath))
        {
            throw new HostUnreachableException(
                $"Captr.exe was not found at {exePath}, so no recording host could be started.");
        }

        // Detached: the host must survive this CLI process ending (SPEC §4).
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            ArgumentList = { "--host" },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        await IpcProtocol.WriteAsync(_pipe,
            IpcProtocol.Envelope(IpcKinds.Hello, new HelloRequest(IpcProtocol.Version, _clientVersion)),
            cancellationToken).ConfigureAwait(false);

        IpcEnvelope? reply = await IpcProtocol.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new HostUnreachableException("The host closed the connection during the handshake.");
        HelloResponse response = reply.PayloadAs<HelloResponse>()
            ?? throw new HostUnreachableException("The host's handshake reply was unreadable.");

        if (!response.Accepted)
        {
            throw new ProtocolMismatchException(response.RefusalReason
                ?? "The host refused the connection (protocol version mismatch).");
        }
    }

    /// <summary>Sends a request and returns the typed response. Throws
    /// <see cref="IpcRequestException"/> when the host answers with an error.</summary>
    public async Task<TResponse> RequestAsync<TResponse>(
        string kind, object? payload, CancellationToken cancellationToken)
    {
        await IpcProtocol.WriteAsync(_pipe, IpcProtocol.Envelope(kind, payload), cancellationToken).ConfigureAwait(false);

        IpcEnvelope reply = await IpcProtocol.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new HostUnreachableException("The host closed the connection mid-request.");

        if (reply.Kind == IpcKinds.Error)
        {
            throw new IpcRequestException(reply.PayloadAs<ErrorResponse>()?.Message ?? "The host reported an error.");
        }

        return reply.PayloadAs<TResponse>()
            ?? throw new IpcRequestException($"The host's '{reply.Kind}' response was unreadable.");
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}

/// <summary>No host answered and none could be started — CLI exit code 3.</summary>
public sealed class HostUnreachableException(string message) : Exception(message);

/// <summary>Client and host speak different protocol versions (SPEC §4: fail
/// clearly rather than misbehave).</summary>
public sealed class ProtocolMismatchException(string message) : Exception(message);

/// <summary>The host answered a request with an error.</summary>
public sealed class IpcRequestException(string message) : Exception(message);
