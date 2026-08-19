using Captr.Core.Ipc;

namespace Captr.App.Services;

/// <summary>
/// The UI's only line to the recording host: polls status once a second and sends
/// commands. Owns connection lifecycle so view models never see a pipe. Holds NO
/// recording state of its own — every field of <see cref="LatestStatus"/> came from
/// the host moments ago, which is what makes killing or restarting the UI harmless
/// (SPEC §4).
/// </summary>
public sealed class HostConnection : IAsyncDisposable
{
    private static readonly StatusResponse IdleStatus =
        new("idle", null, null, null, null, null, null, null, null);

    private readonly string _clientVersion;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pollLoop;

    public HostConnection(string clientVersion) => _clientVersion = clientVersion;

    /// <summary>The most recent status from the host ("idle" when none runs).</summary>
    public StatusResponse LatestStatus { get; private set; } = IdleStatus;

    /// <summary>Raised on the thread pool after every poll; subscribers marshal to
    /// the dispatcher themselves.</summary>
    public event Action<StatusResponse>? StatusChanged;

    /// <summary>Starts the 1 Hz status poll.</summary>
    public void Start() => _pollLoop = PollLoopAsync(_shutdown.Token);

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            StatusResponse status = IdleStatus;
            try
            {
                await using IpcClient? client = await IpcClient.ConnectAsync(
                    _clientVersion, startHostIfNeeded: false, null, cancellationToken).ConfigureAwait(false);
                if (client is not null)
                {
                    status = await client.RequestAsync<StatusResponse>(IpcKinds.Status, null, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IpcRequestException or HostUnreachableException or IOException)
            {
                // The host went away between connect and request — idle it is.
            }
            catch (OperationCanceledException)
            {
                return;
            }

            LatestStatus = status;
            StatusChanged?.Invoke(status);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Sends one request, summoning a host when the operation needs one.
    /// Returns the response, or throws with the host's message.</summary>
    public async Task<TResponse> RequestAsync<TResponse>(
        string kind, object? payload, bool startHostIfNeeded, CancellationToken cancellationToken)
    {
        await using IpcClient? client = await IpcClient.ConnectAsync(
            _clientVersion, startHostIfNeeded, null, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            throw new HostUnreachableException("No recording host is running.");
        }

        return await client.RequestAsync<TResponse>(kind, payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_pollLoop is not null)
        {
#pragma warning disable VSTHRD003 // no synchronisation context in play
            await _pollLoop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        _shutdown.Dispose();
    }
}
