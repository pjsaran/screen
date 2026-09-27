using Captr.Core.Transfers;

using Shouldly;

namespace Captr.Core.Tests.Transfers;

/// <summary>The one line about transfers that Home shows, so a stuck transfer is
/// seen without opening the Transfers page.</summary>
public class TransferDigestTests
{
    private static TransferItem Row(string state) =>
        new(1, @"C:\r\a.mkv", "archive", null, null, state, 1, null, null, 0, 0, 0, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Nothing_to_say_when_everything_arrived_or_nothing_was_sent()
    {
        TransferDigest.Describe([]).ShouldBeEmpty();
        TransferDigest.Describe([Row(TransferQueue.StateCompleted), Row(TransferQueue.StateCancelled)]).ShouldBeEmpty();
    }

    [Fact]
    public void Transfers_on_their_way_and_those_needing_a_person_are_counted_separately()
    {
        string line = TransferDigest.Describe(
        [
            Row(TransferQueue.StatePending), Row(TransferQueue.StateInProgress),
            Row(TransferQueue.StatePausedAuth),
        ]);

        line.ShouldBe("2 transfers on their way · 1 transfer needs attention — see Transfers");
    }
}
