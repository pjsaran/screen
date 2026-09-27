using Captr.Core.Hosting;
using Captr.Core.Ipc;
using Captr.Core.Sessions;
using Captr.Core.Settings;
using Captr.Core.Settings.Migrations;
using Captr.Core.Transfers;

using Serilog.Core;

using Shouldly;

namespace Captr.Core.Tests.Hosting;

/// <summary>
/// "Send again": only the recording's own files, and never a second copy of one
/// that is already on its way.
/// </summary>
public sealed class ResendTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-resend-").FullName;
    private readonly SettingsStore _settings;
    private readonly TransferQueue _queue;

    public ResendTests()
    {
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"), SettingsMigrator.Default);
        _settings.Save(CaptrSettings.CreateDefault() with
        {
            WorkingFolder = Path.Combine(_dir, "Sessions"),
            Destinations = [new DestinationSettings { Name = "archive", Kind = DestinationKind.Folder, FolderPath = Path.Combine(_dir, "archive") }],
        });
        _queue = new TransferQueue(Path.Combine(_dir, "transfers.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private HostService Host() => new(_settings, null, Logger.None, _queue, null,
        (_, _, _) => throw new InvalidOperationException("no recording in this test"));

    /// <summary>A finalised recording whose integrity record names the given outputs.</summary>
    private string Recording(params string[] outputNames)
    {
        string folder = Path.Combine(_dir, "Sessions", "s1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "recording.mkv"), "footage");
        new IntegrityRecord
        {
            SessionId = Guid.NewGuid(),
            FinalizedUtc = DateTimeOffset.UtcNow,
            Segments = [],
            Outputs = [.. outputNames.Select(name => new HashedFile(name, 7, "00"))],
            Coverage = new CoverageReport(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.Zero, []),
            RepairedSegments = 0,
            Notes = [],
        }.Write(folder);
        return folder;
    }

    [Fact]
    public async Task An_integrity_record_naming_a_file_outside_the_recording_queues_nothing()
    {
        // Someone who can write to the working folder edits integrity.json so that
        // "Send again" uploads a file of theirs choosing to every destination.
        string secret = Path.Combine(_dir, "id_rsa");
        await File.WriteAllTextAsync(secret, "private key", TestContext.Current.CancellationToken);
        string folder = Recording(secret, @"..\..\id_rsa");

        ResendResponse response = await Host().ResendAsync(new ResendRequest(folder), TestContext.Current.CancellationToken);

        response.Queued.ShouldBe(0);
        _queue.List().ShouldBeEmpty();
    }

    [Fact]
    public async Task Verify_reports_an_outside_file_name_as_tampering_instead_of_hashing_it()
    {
        string secret = Path.Combine(_dir, "id_rsa");
        await File.WriteAllTextAsync(secret, "private key", TestContext.Current.CancellationToken);
        string folder = Recording(secret);

        IReadOnlyList<string> problems = await IntegrityRecord.ReadOrNull(folder)!.VerifyAsync(folder, TestContext.Current.CancellationToken);

        problems.ShouldHaveSingleItem().ShouldContain("not a file in this recording");
    }

    [Fact]
    public async Task Pressing_send_again_twice_queues_one_transfer()
    {
        string folder = Recording("recording.mkv");
        HostService host = Host();

        (await host.ResendAsync(new ResendRequest(folder), TestContext.Current.CancellationToken)).Queued.ShouldBe(1);
        ResendResponse second = await host.ResendAsync(new ResendRequest(folder), TestContext.Current.CancellationToken);

        second.Queued.ShouldBe(0);
        second.Message.ShouldContain("Already on its way to: archive");
        _queue.List().Count.ShouldBe(1, "a second row would upload a second copy");
    }

    [Fact]
    public async Task Send_again_on_a_stopped_transfer_brings_that_transfer_back()
    {
        string folder = Recording("recording.mkv");
        long stopped = _queue.Enqueue(Path.Combine(folder, "recording.mkv"), "archive");
        _queue.Cancel(stopped);

        (await Host().ResendAsync(new ResendRequest(folder), TestContext.Current.CancellationToken)).Queued.ShouldBe(1);

        TransferItem only = _queue.List().ShouldHaveSingleItem("re-armed, not duplicated - and nothing stuck to hold the recording back from retention");
        only.Id.ShouldBe(stopped);
        only.State.ShouldBe(TransferQueue.StatePending);
    }
}
