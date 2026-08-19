using Captr.Core.Ipc;

using NSubstitute;

using Serilog.Core;

using Shouldly;

namespace Captr.Core.Tests.Ipc;

/// <summary>
/// IPC round-trips over the real named pipe (SPEC §14 unit list): typed
/// request/response, a version mismatch failing clearly, and unknown message kinds
/// being answered rather than fatal.
/// </summary>
public class IpcRoundTripTests : IAsyncLifetime
{
    private readonly IHostOperations _operations = Substitute.For<IHostOperations>();
    private IpcServer _server = null!;

    public ValueTask InitializeAsync()
    {
        _operations.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new StatusResponse("idle", null, null, null, null, null, null, null, null));
        _server = new IpcServer(_operations, "1.2.3-test", Logger.None);
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private static Task<IpcClient?> ConnectAsync() =>
        IpcClient.ConnectAsync("test-client", startHostIfNeeded: false, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_status_request_round_trips_with_a_typed_response()
    {
        await using IpcClient? client = await ConnectAsync();
        client.ShouldNotBeNull();

        StatusResponse status = await client.RequestAsync<StatusResponse>(
            IpcKinds.Status, null, TestContext.Current.CancellationToken);

        status.State.ShouldBe("idle");
    }

    [Fact]
    public async Task A_start_request_carries_its_payload_to_the_host()
    {
        _operations.StartAsync(Arg.Any<StartRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new StartResponse(false, Guid.NewGuid(), "started"));

        await using IpcClient? client = await ConnectAsync();
        StartResponse response = await client!.RequestAsync<StartResponse>(
            IpcKinds.Start, new StartRequest(20, "balanced", "my-label"), TestContext.Current.CancellationToken);

        response.Message.ShouldBe("started");
        await _operations.Received(1).StartAsync(
            Arg.Is<StartRequest>(r => r.FrameRate == 20 && r.QualityPreset == "balanced" && r.Label == "my-label"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_protocol_version_mismatch_fails_with_a_message_naming_both_versions()
    {
        // Hand-rolled hello with a wrong version — simulating a stale client.
        var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", IpcProtocol.PipeName(), System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await using (pipe.ConfigureAwait(false))
        {
            await pipe.ConnectAsync(TestContext.Current.CancellationToken);
            await IpcProtocol.WriteAsync(pipe,
                IpcProtocol.Envelope(IpcKinds.Hello, new HelloRequest(999, "stale-ui")),
                TestContext.Current.CancellationToken);

            IpcEnvelope? reply = await IpcProtocol.ReadAsync(pipe, TestContext.Current.CancellationToken);

            HelloResponse response = reply.ShouldNotBeNull().PayloadAs<HelloResponse>().ShouldNotBeNull();
            response.Accepted.ShouldBeFalse();
            response.RefusalReason.ShouldNotBeNull();
            response.RefusalReason.ShouldContain("999");
            response.RefusalReason.ShouldContain(IpcProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task An_unknown_message_kind_gets_an_error_response_and_the_connection_survives()
    {
        await using IpcClient? client = await ConnectAsync();

        // A kind from some future version: answered with an error, not a hang or drop.
        IpcRequestException exception = await Should.ThrowAsync<IpcRequestException>(() =>
            client!.RequestAsync<StatusResponse>("quantum-entangle", null, TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("quantum-entangle");

        // The same connection still works afterwards.
        StatusResponse status = await client!.RequestAsync<StatusResponse>(
            IpcKinds.Status, null, TestContext.Current.CancellationToken);
        status.State.ShouldBe("idle");
    }

    [Fact]
    public async Task Connecting_without_a_running_host_returns_null_rather_than_spawning_one()
    {
        await _server.DisposeAsync();
        // Give the pipe a moment to disappear.
        await Task.Delay(200, TestContext.Current.CancellationToken);

        IpcClient? client = await IpcClient.ConnectAsync(
            "test", startHostIfNeeded: false, null, TestContext.Current.CancellationToken);

        client.ShouldBeNull();
    }
}
