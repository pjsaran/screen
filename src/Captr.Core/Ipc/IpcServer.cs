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
    private readonly ILogger _log;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;

    public IpcServer(IHostOperations operations, string hostVersion, ILogger log)
    {
        _operations = operations;
        _hostVersion = hostVersion;
        _log = log.ForContext<IpcServer>();
    }

    /// <summary>Starts accepting connections. Returns immediately.</summary>
    public void Start() => _acceptLoop = AcceptLoopAsync(_shutdown.Token);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        string pipeName = IpcProtocol.PipeName();
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = CreateSecuredPipe(pipeName);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Dispose the listening instance, or it would linger and accept a
                // client that nobody will ever serve — a client-side hang this
                // exact bug once caused in the test suite.
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException exception)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _log.Warning(exception, "Pipe accept failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            // One task per client; a hung client cannot block new connections.
            _ = ServeClientAsync(pipe, cancellationToken);
        }
    }

    /// <summary>Pipe admitting ONLY the current user (SPEC §4: restricted to the
    /// current user's SID; reject any other identity).</summary>
    private static NamedPipeServerStream CreateSecuredPipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
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
            catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
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
                    await _operations.StartAsync(envelope.PayloadAs<StartRequest>() ?? new StartRequest(null, null, null), cancellationToken).ConfigureAwait(false)),
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
