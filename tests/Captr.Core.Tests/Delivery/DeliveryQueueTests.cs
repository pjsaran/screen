using Captr.Core.Delivery;

using Shouldly;

namespace Captr.Core.Tests.Delivery;

public class DeliveryQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-queue-").FullName;

    private DeliveryQueue MakeQueue() => new(Path.Combine(_dir, "delivery.db"));

    [Fact]
    public void An_enqueued_item_is_due_immediately()
    {
        DeliveryQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "archive");

        DeliveryItem item = queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
        item.Id.ShouldBe(id);
        item.State.ShouldBe("pending");
        item.ConfirmedOffset.ShouldBe(0);
    }

    [Fact]
    public void Upload_progress_survives_a_new_queue_instance_like_a_host_restart()
    {
        long id = MakeQueue().Enqueue(@"C:\out\rec.mkv", "sp");
        MakeQueue().RecordProgress(id, "https://upload/session1", 5_242_880);

        DeliveryItem item = MakeQueue().NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
        item.UploadUrl.ShouldBe("https://upload/session1");
        item.ConfirmedOffset.ShouldBe(5_242_880);
    }

    [Fact]
    public void A_transient_failure_schedules_a_backed_off_retry()
    {
        DeliveryQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "sp");
        var now = DateTimeOffset.UtcNow;

        queue.Fail(id, FailureKind.Transient, "503 slow down", now, retryAfter: null);

        queue.NextDue(now).ShouldBeNull("not due until the backoff elapses");
        DeliveryItem item = queue.List().Single();
        item.State.ShouldBe("pending");
        item.Attempts.ShouldBe(1);
        item.NextAttemptUtc.ShouldNotBeNull();
        item.NextAttemptUtc.Value.ShouldBeGreaterThan(now);
        queue.NextDue(now + TimeSpan.FromHours(1)).ShouldNotBeNull("due after the backoff");
    }

    [Fact]
    public void A_permanent_failure_parks_in_manual_retry_with_the_servers_words()
    {
        DeliveryQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "sp");
        const string serverMessage = """403 Forbidden: {"error":{"code":"accessDenied","message":"Site quota exceeded"}}""";

        queue.Fail(id, FailureKind.Permanent, serverMessage, DateTimeOffset.UtcNow, null);

        DeliveryItem item = queue.List().Single();
        item.State.ShouldBe("manual-retry");
        item.LastError.ShouldBe(serverMessage);
        queue.NextDue(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)).ShouldBeNull("manual-retry never auto-runs");
    }

    [Fact]
    public void An_auth_failure_pauses_until_resumed()
    {
        DeliveryQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "sp");

        queue.Fail(id, FailureKind.AuthExpired, "token expired", DateTimeOffset.UtcNow, null);
        queue.List().Single().State.ShouldBe("paused-auth");

        queue.ResumeAuthPaused();
        queue.List().Single().State.ShouldBe("pending");
    }

    [Fact]
    public void Manual_retry_re_arms_a_parked_item()
    {
        DeliveryQueue queue = MakeQueue();
        long id = queue.Enqueue(@"C:\out\rec.mkv", "sp");
        queue.Fail(id, FailureKind.Permanent, "denied", DateTimeOffset.UtcNow, null);

        queue.Retry(id);

        queue.NextDue(DateTimeOffset.UtcNow).ShouldNotBeNull();
    }

    [Fact]
    public void Completion_is_tracked_per_destination()
    {
        DeliveryQueue queue = MakeQueue();
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
        DeliveryQueue.Backoff(attempts: 8, serverRetryAfter: TimeSpan.FromSeconds(42))
            .ShouldBe(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void Backoff_grows_but_is_bounded()
    {
        DeliveryQueue.Backoff(1, null).ShouldBeLessThan(TimeSpan.FromMinutes(1));
        DeliveryQueue.Backoff(50, null).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(45));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
