namespace Captr.Core.Ipc;

/// <summary>
/// What the recording host can be asked to do over IPC. The host implements this;
/// the IPC server is just plumbing between the pipe and these methods, which keeps
/// protocol handling and recording logic in separate, separately-testable places.
/// </summary>
public interface IHostOperations
{
    Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken);

    Task<StopResponse> StopAsync(CancellationToken cancellationToken);

    Task<StateResponse> PauseAsync(CancellationToken cancellationToken);

    Task<StateResponse> ResumeAsync(CancellationToken cancellationToken);

    Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken);

    Task<ListRecordingsResponse> ListRecordingsAsync(CancellationToken cancellationToken);

    Task<VerifyResponse> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken);

    Task<RecoverResponse> RecoverAsync(CancellationToken cancellationToken);

    Task<ListTransfersResponse> ListTransfersAsync(CancellationToken cancellationToken);

    Task<StateResponse> RetryTransferAsync(RetryTransferRequest request, CancellationToken cancellationToken);

    /// <summary>Stop a queued, running, or backing-off transfer.</summary>
    Task<StateResponse> CancelTransferAsync(CancelTransferRequest request, CancellationToken cancellationToken);

    Task<ResendResponse> ResendAsync(ResendRequest request, CancellationToken cancellationToken);

    Task<SetSettingsResponse> SetSettingsAsync(SetSettingsRequest request, CancellationToken cancellationToken);
}
