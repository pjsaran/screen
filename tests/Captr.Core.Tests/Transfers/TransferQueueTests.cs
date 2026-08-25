using Captr.Core.Settings;
using Captr.Core.Transfers;

using Shouldly;

namespace Captr.Core.Tests.Transfers;

public class TransferQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-queue-").FullName;

    private TransferQueue MakeQueue() => new(Path.Combine(_dir, "transfers.db"));

    [Fact]
    public void An_enqueued_item_is_due_immediately()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "archive");

        TransferItem item = queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
        item.Id.ShouldBe(id);
        item.State.ShouldBe("pending");
        item.ConfirmedOffset.ShouldBe(0);
    }

    [Fact]
    public void A_recording_going_to_two_destinations_is_two_independent_transfers()
    {
        // This is what makes "retry" mean "re-send only what failed": the queue
        // holds one row per (recording, destination), so one destination succeeding
        // and the other failing are separate facts that cannot affect each other.
        TransferQueue queue = MakeQueue();
        long toArchive = queue.Enqueue(@"C:\out\rec.mkv", "archive");
        long toSharePoint = queue.Enqueue(@"C:\out\rec.mkv", "sharepoint");

        queue.Complete(toArchive);
        queue.Fail(toSharePoint, FailureKind.Permanent, "403 Forbidden", DateTimeOffset.UtcNow, null);
        queue.Retry(toSharePoint);

        List<TransferItem> items = [.. queue.List()];
        items.Single(i => i.Id == toArchive).State.ShouldBe("completed", "a retry must not disturb a transferred copy");
        items.Single(i => i.Id == toSharePoint).State.ShouldBe("pending");
        queue.AllCompletedFor(@"C:\out\rec.mkv").ShouldBeFalse("one destination is still outstanding");
    }

    [Fact]
    public void A_destination_can_give_the_transferred_copy_its_own_name()
    {
        // The workflow may rename a recording per destination (an archive with a
        // different convention from the working copy); the name travels with the
        // transfer so a resumed upload keeps using it.
        TransferQueue queue = MakeQueue();
        queue.Enqueue(@"C:\out\rec.mkv", "archive", "2026-08-20 morning.mkv");

        TransferItem item = queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
        item.TargetName.ShouldBe("2026-08-20 morning.mkv");
    }

    [Fact]
    public void A_destination_without_a_pattern_keeps_the_recording_s_own_name()
    {
        TransferQueue queue = MakeQueue();
        queue.Enqueue(@"C:\out\rec.mkv", "archive");

        queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull().TargetName.ShouldBeNull();
    }

    [Fact]
    public void Upload_progress_survives_a_new_queue_instance_like_a_host_restart()
    {
        long id = MakeQueue().Enqueue(@"C:\out\rec.mkv", "sp");
        MakeQueue().RecordProgress(id, "https://upload/session1", 5_242_880);

        TransferItem item = MakeQueue().NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
        item.UploadUrl.ShouldBe("https://upload/session1");
        item.ConfirmedOffset.ShouldBe(5_242_880);
    }

    [Fact]
    public void A_transient_failure_schedules_a_backed_off_retry()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "sp");
        var now = DateTimeOffset.UtcNow;

        queue.Fail(id, FailureKind.Transient, "503 slow down", now, retryAfter: null);

        queue.NextDue(now).ShouldBeNull("not due until the backoff elapses");
        TransferItem item = queue.List().Single();
        item.State.ShouldBe("pending");
        item.Attempts.ShouldBe(1);
        item.NextAttemptUtc.ShouldNotBeNull();
        item.NextAttemptUtc.Value.ShouldBeGreaterThan(now);
        queue.NextDue(now + TimeSpan.FromHours(1)).ShouldNotBeNull("due after the backoff");
    }

    [Fact]
    public void A_permanent_failure_parks_in_manual_retry_with_the_servers_words()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "sp");
        const string serverMessage = """403 Forbidden: {"error":{"code":"accessDenied","message":"Site quota exceeded"}}""";

        queue.Fail(id, FailureKind.Permanent, serverMessage, DateTimeOffset.UtcNow, null);

        TransferItem item = queue.List().Single();
        item.State.ShouldBe("manual-retry");
        item.LastError.ShouldBe(serverMessage);
        queue.NextDue(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)).ShouldBeNull("manual-retry never auto-runs");
    }

    [Fact]
    public void An_auth_failure_pauses_until_resumed()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "sp");

        queue.Fail(id, FailureKind.AuthExpired, "token expired", DateTimeOffset.UtcNow, null);
        queue.List().Single().State.ShouldBe("paused-auth");

        queue.ResumeAuthPaused();
        queue.List().Single().State.ShouldBe("pending");
    }

    [Fact]
    public void Manual_retry_re_arms_a_parked_item()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "sp");
        queue.Fail(id, FailureKind.Permanent, "denied", DateTimeOffset.UtcNow, null);

        queue.Retry(id);

        queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
    }

    [Fact]
    public void Completion_is_tracked_per_destination()
    {
        TransferQueue queue = MakeQueue();
        long first = queue.Enqueue(@"C:\out\rec.mkv", "archive");
        long second = queue.Enqueue(@"C:\out\rec.mkv", "sp");

        queue.Complete(first);
        queue.AllCompletedFor(@"C:\out\rec.mkv").ShouldBeFalse("the SharePoint copy is still pending");

        queue.Complete(second);
        queue.AllCompletedFor(@"C:\out\rec.mkv").ShouldBeTrue();
    }

    [Fact]
    public void The_servers_retry_after_beats_the_computed_backoff()
    {
        TransferQueue.Backoff(attempt: 8, serverRetryAfter: TimeSpan.FromSeconds(42), Retries)
            .ShouldBe(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void Backoff_grows_but_is_bounded_by_the_configured_ceiling()
    {
        // The first retry is close to the configured start, later ones grow, and
        // nothing ever exceeds the ceiling plus the +20% jitter.
        TransferQueue.Backoff(1, null, Retries).ShouldBeLessThan(TimeSpan.FromSeconds(40));
        TransferQueue.Backoff(4, null, Retries).ShouldBeGreaterThan(TimeSpan.FromSeconds(60));
        TransferQueue.Backoff(50, null, Retries).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(36));
    }

    [Fact]
    public void A_transfer_that_runs_out_of_attempts_stops_retrying_and_asks_for_a_person()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out
ec.mkv", "archive");

        // Three attempts are allowed; the third failure is the one that gives up.
        var policy = new RetrySettings { MaxAttempts = 3 };
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            queue.Fail(id, FailureKind.Transient, "the share went away", DateTimeOffset.UtcNow, null, policy);
        }

        TransferItem item = queue.List().Single();
        item.State.ShouldBe(TransferQueue.StateManualRetry);
        item.Attempts.ShouldBe(3);
        item.LastError.ShouldNotBeNull().ShouldContain("Gave up after 3 automatic attempts");
        queue.NextDue(DateTimeOffset.UtcNow.AddDays(1)).ShouldBeNull("a parked transfer must not retry by itself");
    }

    [Fact]
    public void Retrying_by_hand_clears_the_attempt_count_so_the_run_starts_again()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out
ec.mkv", "archive");
        var policy = new RetrySettings { MaxAttempts = 2 };
        queue.Fail(id, FailureKind.Transient, "boom", DateTimeOffset.UtcNow, null, policy);
        queue.Fail(id, FailureKind.Transient, "boom", DateTimeOffset.UtcNow, null, policy);

        queue.Retry(id);

        TransferItem item = queue.List().Single();
        item.State.ShouldBe(TransferQueue.StatePending);
        item.Attempts.ShouldBe(0);
        queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
    }

    [Fact]
    public void Stopping_a_transfer_takes_it_out_of_the_queue_until_a_person_retries_it()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out
ec.mkv", "archive");

        queue.Cancel(id);

        queue.List().Single().State.ShouldBe(TransferQueue.StateCancelled);
        queue.NextDue(DateTimeOffset.UtcNow).ShouldBeNull();

        queue.Retry(id);
        queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
    }

    [Fact]
    public void A_completed_transfer_cannot_be_stopped()
    {
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out
ec.mkv", "archive");
        queue.Complete(id);

        queue.Cancel(id);

        queue.List().Single().State.ShouldBe(TransferQueue.StateCompleted);
    }

    [Fact]
    public void The_queue_remembers_which_destinations_already_have_a_file()
    {
        TransferQueue queue = MakeQueue();
        long archive = queue.Enqueue(@"C:\out
ec.mkv", "archive");
        queue.Enqueue(@"C:\out
ec.mkv", "sharepoint");

        queue.Complete(archive);

        queue.CompletedDestinationsFor(@"C:\out
ec.mkv").ShouldBe(["archive"]);
    }

    [Fact]
    public void The_expanded_target_folder_is_remembered_with_the_transfer()
    {
        TransferQueue queue = MakeQueue();
        queue.Enqueue(@"C:\out
ec.mkv", "archive", "renamed.mkv", @"\nasideo6-08");

        TransferItem item = queue.List().Single();
        item.TargetName.ShouldBe("renamed.mkv");
        item.TargetFolder.ShouldBe(@"\nasideo6-08");
    }

    /// <summary>The shipped defaults, used wherever a test is not about the policy
    /// itself.</summary>
    private static RetrySettings Retries => new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    // ---- Keeping the queue small -------------------------------------------------
    //
    // The queue used to grow for ever: one row per (recording, destination), never
    // removed. The tests below use a FUTURE "now" rather than back-dating rows, which
    // is the same thing from the query's point of view and needs no direct SQL.

    private static DateTimeOffset LongAfter(TimeSpan window) => DateTimeOffset.UtcNow + window + TimeSpan.FromDays(1);

    [Fact]
    public void The_page_stops_listing_finished_transfers_once_they_are_old()
    {
        TransferQueue queue = MakeQueue();
        queue.Complete(queue.Enqueue(@"C:\out\rec.mkv", "archive"));

        queue.ListRecent(DateTimeOffset.UtcNow).ShouldHaveSingleItem();
        queue.ListRecent(LongAfter(TransferQueue.HistoryWindow)).ShouldBeEmpty();
    }

    [Fact]
    public void An_unfinished_transfer_is_listed_however_old_it_is()
    {
        // A transfer stuck for two months is exactly the one somebody needs to see,
        // so the age window must never hide it.
        TransferQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "archive");
        queue.Fail(id, FailureKind.Permanent, "403 Forbidden", DateTimeOffset.UtcNow, null);

        queue.ListRecent(LongAfter(TransferQueue.HistoryWindow)).ShouldHaveSingleItem();
    }

    [Fact]
    public void Pruning_removes_old_finished_rows_whose_recording_is_gone()
    {
        TransferQueue queue = MakeQueue();
        queue.Complete(queue.Enqueue(Path.Combine(_dir, "vanished.mkv"), "archive"));

        queue.Prune(LongAfter(TransferQueue.HistoryWindow)).ShouldBe(1);
        queue.List().ShouldBeEmpty();
    }

    [Fact]
    public void Pruning_keeps_the_history_of_a_recording_that_is_still_on_disk()
    {
        // RetentionCleaner decides whether a session folder may go by asking the queue
        // what completed. Throwing that away while the file is still there would make
        // it conclude the recording had never been transferred, and keep it for ever.
        TransferQueue queue = MakeQueue();
        string stillHere = Path.Combine(_dir, "still-here.mkv");
        File.WriteAllText(stillHere, "not really a recording, but it exists");
        queue.Complete(queue.Enqueue(stillHere, "archive"));

        queue.Prune(LongAfter(TransferQueue.HistoryWindow)).ShouldBe(0);
        queue.List().ShouldHaveSingleItem();
    }

    [Fact]
    public void Pruning_never_touches_a_transfer_that_has_not_finished()
    {
        TransferQueue queue = MakeQueue();
        queue.Enqueue(Path.Combine(_dir, "vanished.mkv"), "archive");

        queue.Prune(LongAfter(TransferQueue.HistoryWindow)).ShouldBe(0);
        queue.List().ShouldHaveSingleItem();
    }
}
