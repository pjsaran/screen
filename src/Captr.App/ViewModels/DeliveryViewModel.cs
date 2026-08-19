using Captr.App.Services;
using Captr.Core.Ipc;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Captr.App.ViewModels;

/// <summary>
/// The delivery page: every pending and failed transfer with attempt count and the
/// server's verbatim error beside a plain-English explanation, with retry (SPEC §9).
/// </summary>
public sealed partial class DeliveryViewModel : ObservableObject
{
    private readonly HostConnection _host;

    public System.Collections.ObjectModel.ObservableCollection<DeliveryRow> Deliveries { get; } = [];

    [ObservableProperty]
    private string _message = "";

    public DeliveryViewModel(HostConnection host) => _host = host;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            ListDeliveriesResponse response = await _host.RequestAsync<ListDeliveriesResponse>(
                IpcKinds.ListDeliveries, null, startHostIfNeeded: true, CancellationToken.None);
            Deliveries.Clear();
            foreach (DeliverySummary summary in response.Deliveries)
            {
                Deliveries.Add(new DeliveryRow(summary));
            }

            Message = Deliveries.Count == 0 ? "No deliveries yet." : "";
        }
        catch (Exception exception) when (exception is HostUnreachableException or IpcRequestException)
        {
            Message = exception.Message;
        }
    }

    [RelayCommand]
    private async Task RetryAsync(DeliveryRow row)
    {
        try
        {
            await _host.RequestAsync<StateResponse>(
                IpcKinds.RetryDelivery, new RetryDeliveryRequest(row.Id), startHostIfNeeded: true, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is HostUnreachableException or IpcRequestException)
        {
            Message = exception.Message;
        }
    }
}

/// <summary>One transfer row, with the plain-English reading of its state.</summary>
public sealed record DeliveryRow
{
    public DeliveryRow(DeliverySummary summary)
    {
        Id = summary.Id;
        File = Path.GetFileName(summary.OutputPath);
        Destination = summary.DestinationName;
        State = summary.State;
        Attempts = summary.Attempts;
        ServerError = summary.LastError ?? "";
        Explanation = summary.State switch
        {
            "completed" => "Delivered and verified.",
            "pending" => summary.NextAttemptUtc is { } next
                ? $"Will retry at {next.ToLocalTime():HH:mm:ss}."
                : "Waiting to transfer.",
            "in-progress" => "Transferring now.",
            "paused-auth" => "Paused: the stored credential no longer works. Re-provision it, then retry.",
            "manual-retry" => "Stopped: the destination refused this transfer (see the server's message). Fix the cause, then retry.",
            _ => summary.State,
        };
    }

    public long Id { get; }

    public string File { get; }

    public string Destination { get; }

    public string State { get; }

    public int Attempts { get; }

    public string ServerError { get; }

    public string Explanation { get; }

    public bool CanRetry => State is "manual-retry" or "paused-auth" or "pending";
}
