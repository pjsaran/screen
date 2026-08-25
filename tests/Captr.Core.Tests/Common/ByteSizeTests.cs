using Captr.Core.Common;

using Shouldly;

namespace Captr.Core.Tests.Common;

/// <summary>
/// Sizes shown to people. The unit has to follow the number, or a short recording
/// reads as "0 MB" and looks like a failure rather than a small file.
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(847, "847 B")]
    [InlineData(309_583, "310 KB")]
    [InlineData(1_400_000, "1.4 MB")]
    [InlineData(309_000_000, "309 MB")]
    [InlineData(12_700_000_000, "12.70 GB")]
    public void A_size_is_shown_in_a_unit_that_carries_information(long bytes, string expected) =>
        ByteSize.Format(bytes).ShouldBe(expected);

    [Fact]
    public void A_small_recording_never_reads_as_zero()
    {
        // The bug this guards: 309 KB formatted with a fixed MB unit was "0 MB".
        ByteSize.Format(309_583).ShouldNotStartWith("0");
    }
}
