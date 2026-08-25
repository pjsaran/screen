using Captr.Core.Ipc;

using Shouldly;

namespace Captr.Core.Tests.Ipc;

public class IpcFramingTests
{
    [Fact]
    public async Task An_envelope_round_trips_through_the_frame_format()
    {
        using var stream = new MemoryStream();
        IpcEnvelope original = IpcProtocol.Envelope(IpcKinds.Start, new StartRequest(15, "high", "veryfast", null));

        await IpcProtocol.WriteAsync(stream, original, TestContext.Current.CancellationToken);
        stream.Position = 0;
        IpcEnvelope? read = await IpcProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);

        read.ShouldNotBeNull();
        read.Kind.ShouldBe(IpcKinds.Start);
        StartRequest payload = read.PayloadAs<StartRequest>().ShouldNotBeNull();
        payload.FrameRate.ShouldBe(15);
        payload.Quality.ShouldBe("high");
        payload.SpeedPreset.ShouldBe("veryfast");
    }

    [Fact]
    public async Task A_clean_end_of_stream_reads_as_null()
    {
        using var stream = new MemoryStream();
        (await IpcProtocol.ReadAsync(stream, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task A_corrupt_length_prefix_is_rejected_not_allocated()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F, 0x00]);

        await Should.ThrowAsync<InvalidDataException>(() =>
            IpcProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stream_ending_mid_frame_throws_rather_than_returning_a_half_message()
    {
        using var full = new MemoryStream();
        await IpcProtocol.WriteAsync(full, IpcProtocol.Envelope(IpcKinds.Status), TestContext.Current.CancellationToken);
        using var truncated = new MemoryStream(full.ToArray()[..^3]);

        await Should.ThrowAsync<EndOfStreamException>(() =>
            IpcProtocol.ReadAsync(truncated, TestContext.Current.CancellationToken));
    }
}
