using Captr.Core.Supervision;

using Shouldly;

namespace Captr.Core.Tests.Supervision;

public class EncoderProgressTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_typical_block_parses_completely()
    {
        var progress = EncoderProgress.Parse(
            ["frame=150", "fps=15.02", "total_size=1048576", "out_time_us=10000000", "speed=1.01x", "progress=continue"],
            Now);

        progress.Frame.ShouldBe(150);
        progress.TotalSizeBytes.ShouldBe(1048576);
        progress.OutTime.ShouldBe(TimeSpan.FromSeconds(10));
        progress.Speed.ShouldBe(1.01);
        progress.IsEnd.ShouldBeFalse();
    }

    [Fact]
    public void The_final_block_reports_end()
    {
        EncoderProgress.Parse(["progress=end"], Now).IsEnd.ShouldBeTrue();
    }

    [Fact]
    public void Speed_NA_at_startup_parses_as_null_not_zero()
    {
        // Treating "N/A" as 0 would trigger the slow-encoding warning on every start.
        EncoderProgress.Parse(["speed=N/A", "progress=continue"], Now).Speed.ShouldBeNull();
    }

    [Fact]
    public void Unknown_keys_and_malformed_lines_are_ignored()
    {
        var progress = EncoderProgress.Parse(
            ["frame=5", "some_future_key=xyz", "not a key value line", "=weird", "progress=continue"],
            Now);

        progress.Frame.ShouldBe(5);
    }
}
