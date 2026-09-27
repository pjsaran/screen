using Captr.Core.Secrets;
using Captr.Core.Settings;
using Captr.Core.Settings.Migrations;
using Captr.Core.Transfers;

using Serilog.Core;

using Shouldly;

namespace Captr.Integration.Tests.Transfers;

/// <summary>
/// The transfer worker driving real destinations — the mock Graph server over HTTP,
/// and real folders including a UNC share — through the failures that used to strand
/// a transfer for good.
/// </summary>
[Trait("Category", "Os")]
public sealed class TransferWorkerEndToEndTests : IDisposable
{
    private const string DriveId = "b!x2FakeDriveId";

    private readonly string _dir = Directory.CreateTempSubdirectory("captr-worker-e2e-").FullName;
    private readonly MockGraphServer _server = new();
    private readonly HttpClient _http;
    private readonly TransferQueue _queue;
    private readonly SettingsStore _settings;

    public TransferWorkerEndToEndTests()
    {
        _http = new HttpClient { BaseAddress = _server.BaseUrl };
        _queue = new TransferQueue(Path.Combine(_dir, "transfers.db"));
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"), SettingsMigrator.Default);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeTokens : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("fake-token");
    }

    private void UseDestination(DestinationSettings destination) =>
        _settings.Save(CaptrSettings.CreateDefault() with
        {
            WorkingFolder = Path.Combine(_dir, "Sessions"),
            Destinations = [destination],
        });

    private static DestinationSettings SharePoint() => new()
    {
        Name = "sp",
        Kind = DestinationKind.SharePoint,
        SharePointSiteUrl = "https://contoso.sharepoint.com/sites/rec",
        SharePointDriveId = DriveId,
        TenantId = "00000000-0000-0000-0000-000000000000",
        ClientId = "00000000-0000-0000-0000-000000000000",
        CredentialName = "sp",
    };

    private TransferWorker MakeWorker(Func<DestinationSettings, IAccessTokenProvider>? tokens = null) =>
        new(_queue, _settings, Logger.None, tokens ?? (_ => new FakeTokens()), _http);

    private string Recording(int bytes)
    {
        string path = Path.Combine(_dir, "rec.mkv");
        byte[] content = new byte[bytes];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(path, content);
        return path;
    }

    private async Task<TransferItem> RunOnceAsync(TransferWorker worker)
    {
        TransferItem item = _queue.NextDue(DateTimeOffset.UtcNow.AddHours(1)).ShouldNotBeNull();
        await worker.TransferOneAsync(item, TestContext.Current.CancellationToken);
        return _queue.Find(item.Id)!;
    }

    [Fact]
    public async Task An_upload_whose_session_expired_starts_a_new_session_and_completes()
    {
        // Graph expires an idle upload session. The next attempt used to ask the dead
        // URL where it had got to, keep its stale offset on the 404, send a chunk to
        // the dead URL, book the second 404 as a permanent refusal — and Retry re-used
        // the same dead URL, so the transfer could never complete.
        UseDestination(SharePoint());
        string file = Recording(3 * 327_680 * 8 + 12_345);
        _queue.Enqueue(file, "sp", null, null);
        TransferWorker worker = MakeWorker();

        _server.AbortAfterChunks = 2; // the first chunk is confirmed and persisted, the second is cut off
        TransferItem afterDrop = await RunOnceAsync(worker);
        afterDrop.ConfirmedOffset.ShouldBeGreaterThan(0);

        _server.ExpireCurrentSession();
        TransferItem finished = await RunOnceAsync(worker);

        finished.State.ShouldBe(TransferQueue.StateCompleted, finished.LastError);
        _server.SessionsCreated.ShouldBe(2);
        _server.ReceivedBytes.ShouldBe(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_upload_that_finished_but_lost_its_last_reply_is_recognised_rather_than_sent_twice()
    {
        // The final chunk landed and the file exists in SharePoint; only the 201 was
        // lost. A fresh session would put a second copy beside it as "rec 1.mkv".
        UseDestination(SharePoint());
        string file = Recording(2 * 327_680 * 8 + 777);
        _queue.Enqueue(file, "sp", null, null);
        TransferWorker worker = MakeWorker();

        _server.LoseFinalReply = true;
        TransferItem afterLostReply = await RunOnceAsync(worker);
        afterLostReply.State.ShouldNotBe(TransferQueue.StateCompleted);

        TransferItem finished = await RunOnceAsync(worker);

        finished.State.ShouldBe(TransferQueue.StateCompleted, finished.LastError);
        _server.SessionsCreated.ShouldBe(1, "no second upload of a file that is already there");
    }

    [Fact]
    public async Task A_missing_credential_pauses_the_transfer_for_sign_in_instead_of_killing_the_worker()
    {
        // The credential store throws its own exception type, which the worker did not
        // catch: it escaped, the row stayed "in progress" for ever, and every later
        // transfer queued behind it.
        UseDestination(SharePoint());
        _queue.Enqueue(Recording(1000), "sp", null, null);
        TransferWorker worker = MakeWorker(_ => throw new CredentialNotFoundException(
            "No secret is stored under 'sp'. Store one with: captr auth set-secret sp"));

        TransferItem item = await RunOnceAsync(worker);

        item.State.ShouldBe(TransferQueue.StatePausedAuth);
        item.LastError.ShouldNotBeNull();
        item.LastError.ShouldContain("captr auth set-secret");
    }

    [Fact]
    public async Task A_transfer_stopped_between_being_picked_and_being_claimed_is_not_sent()
    {
        string destinationFolder = Path.Combine(_dir, "dest");
        UseDestination(new DestinationSettings { Name = "folder", Kind = DestinationKind.Folder, FolderPath = destinationFolder });
        _queue.Enqueue(Recording(1000), "folder", null, null);
        TransferWorker worker = MakeWorker();

        TransferItem picked = _queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
        _queue.Cancel(picked.Id);
        await worker.TransferOneAsync(picked, TestContext.Current.CancellationToken);

        _queue.Find(picked.Id)!.State.ShouldBe(TransferQueue.StateCancelled);
        Directory.Exists(destinationFolder).ShouldBeFalse("Stop means nothing is sent");
    }

    [Fact]
    public async Task A_folder_destination_on_a_UNC_share_receives_the_recording()
    {
        // The user guide offers \\server\share\recordings. Checking free space went
        // through DriveInfo, which accepts only drive letters, so every transfer to a
        // share failed with "Drive name must be a root directory".
        string local = Path.Combine(_dir, "unc-dest");
        string unc = @"\\localhost\" + local[0] + "$" + local[2..];
        if (!Directory.Exists(Path.GetDirectoryName(unc)))
        {
            Assert.Skip($"The loopback administrative share {Path.GetPathRoot(unc)} is not reachable on this machine.");
        }

        UseDestination(new DestinationSettings { Name = "share", Kind = DestinationKind.Folder, FolderPath = unc });
        string file = Recording(4096);
        _queue.Enqueue(file, "share", null, null);

        TransferItem item = await RunOnceAsync(MakeWorker());

        item.State.ShouldBe(TransferQueue.StateCompleted, item.LastError);
        (await File.ReadAllBytesAsync(Path.Combine(local, "rec.mkv"), TestContext.Current.CancellationToken))
            .ShouldBe(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Directory.GetFiles(local, "*.partial").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_already_sitting_at_the_partial_name_is_never_overwritten()
    {
        // A destination folder may be shared with other machines or people. Copying
        // through "<name>.partial" with FileMode.Create truncated whatever was there.
        string destinationFolder = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(destinationFolder);
        string bystander = Path.Combine(destinationFolder, "rec.mkv.partial");
        await File.WriteAllTextAsync(bystander, "someone else's copy in flight", TestContext.Current.CancellationToken);
        UseDestination(new DestinationSettings { Name = "folder", Kind = DestinationKind.Folder, FolderPath = destinationFolder });
        _queue.Enqueue(Recording(2048), "folder", null, null);

        TransferItem item = await RunOnceAsync(MakeWorker());

        item.State.ShouldBe(TransferQueue.StateCompleted, item.LastError);
        (await File.ReadAllTextAsync(bystander, TestContext.Current.CancellationToken)).ShouldBe("someone else's copy in flight");
    }

    [Fact]
    public void An_upload_address_that_is_not_https_receives_nothing()
    {
        // The upload URL is itself a write capability. One read back from a tampered
        // queue database must not send a recording anywhere in the clear.
        Should.Throw<TransferException>(() => GraphUploader.RequireSecureUploadUrl("http://evil.example/upload/1"))
            .Kind.ShouldBe(FailureKind.Permanent);
        Should.Throw<TransferException>(() => GraphUploader.RequireSecureUploadUrl("not a url"));
        Should.NotThrow(() => GraphUploader.RequireSecureUploadUrl("https://contoso.sharepoint.com/upload/1"));
        Should.NotThrow(() => GraphUploader.RequireSecureUploadUrl("http://127.0.0.1:5000/upload/1"));
    }
}
