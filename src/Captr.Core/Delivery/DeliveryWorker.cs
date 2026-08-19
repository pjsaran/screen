using Captr.Core.Settings;

using Serilog;

namespace Captr.Core.Delivery;

/// <summary>
/// The host's background loop draining the delivery queue (SPEC §7). Owns dispatch
/// to the right destination kind and the failure classification on the way back.
/// A delivery failure never touches the local file — the worst outcome here is a
/// row parked in manual-retry (SPEC §13 rule 4).
/// </summary>
public sealed class DeliveryWorker
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly DeliveryQueue _queue;
    private readonly SettingsStore _settingsStore;
    private readonly Func<DestinationSettings, IAccessTokenProvider> _tokenProviderFactory;
    private readonly HttpClient _graphHttp;
    private readonly ILogger _log;

    public DeliveryWorker(
        DeliveryQueue queue,
        SettingsStore settingsStore,
        ILogger log,
        Func<DestinationSettings, IAccessTokenProvider>? tokenProviderFactory = null,
        HttpClient? graphHttp = null)
    {
        _queue = queue;
        _settingsStore = settingsStore;
        _log = log.ForContext<DeliveryWorker>();
        _tokenProviderFactory = tokenProviderFactory ?? (destination => new MsalTokenProvider(destination));
        _graphHttp = graphHttp ?? new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
    }

    /// <summary>True while any transfer is pending or running — keeps the host from
    /// idling out mid-delivery.</summary>
    public bool HasPendingWork =>
        _queue.List().Any(i => i.State is "pending" or "in-progress");

    /// <summary>Drains the queue until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DeliveryItem? item = _queue.NextDue(DateTimeOffset.UtcNow);
            if (item is null)
            {
                try
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await DeliverOneAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One attempt at one item; every outcome lands back in the queue.</summary>
    public async Task DeliverOneAsync(DeliveryItem item, CancellationToken cancellationToken)
    {
        _queue.MarkInProgress(item.Id);
        try
        {
            DestinationSettings? destination = _settingsStore.Load()
                .Destinations.FirstOrDefault(d => d.Name == item.DestinationName);
            if (destination is null || !destination.Enabled)
            {
                _queue.Fail(item.Id, FailureKind.Permanent,
                    $"Destination '{item.DestinationName}' no longer exists or is disabled.",
                    DateTimeOffset.UtcNow, null);
                return;
            }

            if (!File.Exists(item.OutputPath))
            {
                _queue.Fail(item.Id, FailureKind.Permanent,
                    $"The local file is missing: {item.OutputPath}", DateTimeOffset.UtcNow, null);
                return;
            }

            switch (destination.Kind)
            {
                case DestinationKind.Folder:
                    string landed = await FolderDestination.DeliverAsync(
                        item.OutputPath, destination.FolderPath!, Path.GetFileName(item.OutputPath),
                        progress: null, cancellationToken).ConfigureAwait(false);
                    _log.Information("Delivered {File} to {Landed}", item.OutputPath, landed);
                    break;

                case DestinationKind.SharePoint:
                    await DeliverToGraphAsync(item, destination, cancellationToken).ConfigureAwait(false);
                    break;
            }

            _queue.Complete(item.Id);
        }
        catch (DeliveryException exception)
        {
            _log.Warning("Delivery {Id} failed ({Kind}): {Message}", item.Id, exception.Kind, exception.Message);
            _queue.Fail(item.Id, exception.Kind, exception.Message, DateTimeOffset.UtcNow, exception.RetryAfter);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or TaskCanceledException)
        {
            // Network hiccups and file locks are transient by definition.
            _log.Warning("Delivery {Id} failed transiently: {Message}", item.Id, exception.Message);
            _queue.Fail(item.Id, FailureKind.Transient, exception.Message, DateTimeOffset.UtcNow, null);
        }
    }

    private async Task DeliverToGraphAsync(
        DeliveryItem item, DestinationSettings destination, CancellationToken cancellationToken)
    {
        var uploader = new GraphUploader(_graphHttp, _tokenProviderFactory(destination));

        string uploadUrl = item.UploadUrl ?? await uploader.CreateSessionAsync(
            $"{destination.SharePointFolder?.Trim('/')}/{Path.GetFileName(item.OutputPath)}".TrimStart('/'),
            cancellationToken).ConfigureAwait(false);

        await uploader.UploadAsync(
            uploadUrl, item.OutputPath, item.ConfirmedOffset,
            // Persist EVERY confirmed chunk (SPEC §7) — this is what an interrupted
            // upload resumes from.
            confirmedOffset => _queue.RecordProgress(item.Id, uploadUrl, confirmedOffset),
            cancellationToken).ConfigureAwait(false);

        _log.Information("Uploaded {File} to SharePoint destination {Destination}",
            item.OutputPath, destination.Name);
    }
}
