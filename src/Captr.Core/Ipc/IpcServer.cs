using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

using Serilog;

namespace Captr.Core.Ipc;

/// <summary>
/// The host's end of the pipe: accepts connections (current user only), enforces
/// the hello handshake, and dispatches requests to <see cref="IHostOperations"/>.
/// Owns connection lifecycle and protocol enforcement; recording logic lives
/// entirely behind the operations interface. A client that misbehaves loses its
/// connection — the host never does.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IHostOperations _operations;
    private readonly string _hostVersion;
    private readonly string? _instanceSuffix;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;

    /// <param name="instanceSuffix">Test seam — see
    /// <see cref="IpcProtocol.PipeName(string?)"/>. Production passes null.</param>
    public IpcServer(IHostOperations operations, string hostVersion, ILogger log, string? instanceSuffix = null)
    {
        _operations = operations;
        _hostVersion = hostVersion;
        _instanceSuffix = instanceSuffix;
        _log = log.ForContext<IpcServer>();
    }

    /// <summary>
    /// Claims the pipe name and starts accepting connections. Returns immediately.
    /// </summary>
    /// <exception cref="PipeNameTakenException">Something else already owns the
    /// name — this user's Captr recorder in another Windows session, or a process
    /// squatting it. The host must not start: it would otherwise serve under the
    /// squatter's security descriptor (see <see cref="CreateSecuredPipe"/>).</exception>
    public void Start()
    {
        string pipeName = IpcProtocol.PipeName(_instanceSuffix);
        NamedPipeServerStream first = CreateSecuredPipe(pipeName, firstInstance: true);
        _acceptLoop = AcceptLoopAsync(pipeName, first, _shutdown.Token);
    }

    private async Task AcceptLoopAsync(string pipeName, NamedPipeServerStream listening, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await listening.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Dispose the listening instance, or it would linger and accept a
                // client that nobody will ever serve — a client-side hang this
                // exact bug once caused in the test suite.
                await listening.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException exception)
            {
                // A client that connected and vanished before the accept completed.
                // The replacement is created BEFORE this instance is disposed, so the
                // name is never momentarily free for someone else to claim.
                _log.Warning(exception, "Pipe accept failed; retrying");
                NamedPipeServerStream replacement = CreateSecuredPipe(pipeName, firstInstance: false);
                await listening.DisposeAsync().ConfigureAwait(false);
                listening = replacement;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            // The next listening instance exists before this one is handed off. A
            // named pipe ceases to exist when its last instance closes, and a client
            // that disconnects instantly would otherwise leave a gap in which another
            // user could create the name with their own security descriptor.
            NamedPipeServerStream connected = listening;
            listening = CreateSecuredPipe(pipeName, firstInstance: false);

            // One task per client; a hung client cannot block new connections.
            _ = ServeClientAsync(connected, cancellationToken);
        }

        await listening.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Pipe admitting ONLY the current user (SPEC §4: restricted to the current
    /// user's SID; reject any other identity).
    /// </summary>
    /// <remarks>
    /// A named pipe's security descriptor belongs to whoever created its FIRST
    /// instance; later instances inherit it, and the descriptor passed for them is
    /// ignored. The name is a hash of the user's SID, which anyone can compute. So the
    /// first instance is created with <see cref="PipeOptions.FirstPipeInstance"/> — if
    /// the name already exists the host refuses, rather than serving under somebody
    /// else's rules. The owner is set to the user explicitly because an elevated
    /// process would otherwise make the Administrators group the owner, and the
    /// client's ownership check would then (correctly) refuse to talk to it.
    /// </remarks>
    private static NamedPipeServerStream CreateSecuredPipe(string pipeName, bool firstInstance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetOwner(identity.User!);
        security.AddAccessRule(new PipeAccessRule(
            identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        PipeOptions options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        try
        {
            return NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options, 0, 0, security);
        }
        catch (UnauthorizedAccessException exception) when (firstInstance)
        {
            throw new PipeNameTakenException(pipeName, exception);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                // Handshake first: version mismatch gets a typed refusal (SPEC §4).
                IpcEnvelope? hello = await IpcProtocol.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (hello is null || hello.Kind != IpcKinds.Hello)
                {
                    return;
                }

                HelloRequest? request = hello.PayloadAs<HelloRequest>();
                if (request is null || request.ProtocolVersion != IpcProtocol.Version)
                {
                    await IpcProtocol.WriteAsync(pipe, IpcProtocol.Envelope(IpcKinds.Hello, new HelloResponse(
                        Accepted: false, IpcProtocol.Version, _hostVersion,
                        $"Protocol version mismatch: client speaks {request?.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}, " +
                        $"host speaks {IpcProtocol.Version}. Update the older side.")), cancellationToken).ConfigureAwait(false);
                    return;
                }

                await IpcProtocol.WriteAsync(pipe, IpcProtocol.Envelope(IpcKinds.Hello,
                    new HelloResponse(true, IpcProtocol.Version, _hostVersion, null)), cancellationToken).ConfigureAwait(false);

                while (await IpcProtocol.ReadAsync(pipe, cancellationToken).ConfigureAwait(false) is { } envelope)
                {
                    IpcEnvelope response = await DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
                    await IpcProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException
                                                  or System.Text.Json.JsonException)
            {
                // A broken or misbehaving client — its problem, not the host's.
                _log.Debug("IPC client disconnected uncleanly: {Reason}", exception.Message);
            }
        }
    }

    private async Task<IpcEnvelope> DispatchAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            return envelope.Kind switch
            {
                IpcKinds.Start => IpcProtocol.Envelope(IpcKinds.Start,
                    await _operations.StartAsync(envelope.PayloadAs<StartRequest>() ?? new StartRequest(null, null, null, null), cancellationToken).ConfigureAwait(false)),
                IpcKinds.Stop => IpcProtocol.Envelope(IpcKinds.Stop,
                    await _operations.StopAsync(cancellationToken).ConfigureAwait(false)),
                IpcKinds.Pause => IpcProtocol.Envelope(IpcKinds.Pause,
                    await _operations.PauseAsync(cancellationToken).ConfigureAwait(false)),
                IpcKinds.Resume => IpcProtocol.Envelope(IpcKinds.Resume,
                    await _operations.ResumeAsync(cancellationToken).ConfigureAwait(false)),
                IpcKinds.Status => IpcProtocol.Envelope(IpcKinds.Status,
                    await _operations.GetStatusAsync(cancellationToken).ConfigureAwait(false)),
                IpcKinds.ListRecordings => IpcProtocol.Envelope(IpcKinds.ListRecordings,
                    await _operations.ListRecordingsAsync(cancellationToken).ConfigureAwait(false)),
                IpcKinds.Verify => IpcProtocol.Envelope(IpcKinds.Verify,
                    await _operations.VerifyAsync(envelope.PayloadAs<VerifyRequest>() ?? new VerifyRequest(""), cancellationToken).ConfigureAwait(false)),
                IpcKinds.Recover => IpcProtocol.Envelope(IpcKinds.Recover,
                    await _operations.RecoverAsync(cancellationToken).ConfigureAwait(false)),
                IpcKinds.ListTransfers => IpcProtocol.Envelope(IpcKinds.ListTransfers,
                    await _operations.ListTransfersAsync(cancellationToken).ConfigureAwait(false)),
                IpcKinds.RetryTransfer => IpcProtocol.Envelope(IpcKinds.RetryTransfer,
                    await _operations.RetryTransferAsync(envelope.PayloadAs<RetryTransferRequest>() ?? new RetryTransferRequest(0), cancellationToken).ConfigureAwait(false)),
                IpcKinds.CancelTransfer => IpcProtocol.Envelope(IpcKinds.CancelTransfer,
                    await _operations.CancelTransferAsync(envelope.PayloadAs<CancelTransferRequest>() ?? new CancelTransferRequest(0), cancellationToken).ConfigureAwait(false)),
                IpcKinds.Resend => IpcProtocol.Envelope(IpcKinds.Resend,
                    await _operations.ResendAsync(envelope.PayloadAs<ResendRequest>() ?? new ResendRequest(""), cancellationToken).ConfigureAwait(false)),
                IpcKinds.SetSettings => IpcProtocol.Envelope(IpcKinds.SetSettings,
                    await _operations.SetSettingsAsync(
                        envelope.PayloadAs<SetSettingsRequest>()
                        ?? throw new InvalidOperationException("set-settings requires a settings payload."),
                        cancellationToken).ConfigureAwait(false)),

                // Unknown kinds are ANSWERED, never fatal (SPEC §14): a newer
                // client's new feature degrades to an error message, not a hang.
                _ => IpcProtocol.Envelope(IpcKinds.Error,
                    new ErrorResponse($"Unknown message kind '{envelope.Kind}' — this host ({_hostVersion}) does not understand it.")),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error(exception, "IPC request {Kind} failed", envelope.Kind);
            return IpcProtocol.Envelope(IpcKinds.Error, new ErrorResponse(exception.Message));
        }
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try
            {
                // VSTHRD003's JoinableTaskFactory deadlock concern does not apply:
                // this host never captures a synchronisation context.
#pragma warning disable VSTHRD003
                await _acceptLoop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }
}

/// <summary>
/// The host's pipe name already exists, so this host must not serve (see
/// <see cref="IpcServer.Start"/>). Either this user already has a Captr recorder in
/// another Windows session, or another program has taken the name.
/// </summary>
public sealed class PipeNameTakenException(string pipeName, Exception inner) : IOException(
    $"Captr's command pipe '{pipeName}' already exists, so this recorder will not start. " +
    "Either a Captr recorder is already running for you in another Windows session " +
    "(for example a scheduled task, or a remote desktop session), or another program " +
    "has taken the name. Captr never shares its pipe.", inner);
