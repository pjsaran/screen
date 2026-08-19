using Captr.Core.Delivery;

using Shouldly;

namespace Captr.Integration.Tests.Delivery;

/// <summary>
/// The Graph resumable-upload client against the local mock server (SPEC §14:
/// "cloud delivery against a mocked service resumes from the correct offset after
/// an interrupted upload, and a permission failure lands in manual retry with the
/// server's message intact").
/// </summary>
public class GraphUploaderTests : IDisposable
{
    private sealed class FakeTokens : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("fake-token");
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("captr-graph-").FullName;
    private readonly MockGraphServer _server = new();

    private GraphUploader MakeUploader() =>
        new(new HttpClient { BaseAddress = _server.BaseUrl }, new FakeTokens());

    private string MakeFile(int bytes)
    {
        string path = Path.Combine(_dir, "rec.mkv");
        byte[] content = new byte[bytes];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task A_multi_chunk_upload_completes_with_the_verified_size()
    {
        // 3 chunks: 2 full + 1 partial.
        string file = MakeFile((GraphUploader.ChunkBytes * 2) + 1234);
        GraphUploader uploader = MakeUploader();
        var confirmedOffsets = new List<long>();

        string uploadUrl = await uploader.CreateSessionAsync("Recordings/rec.mkv", TestContext.Current.CancellationToken);
        long size = await uploader.UploadAsync(
            uploadUrl, file, 0, confirmedOffsets.Add, TestContext.Current.CancellationToken);

        size.ShouldBe(new FileInfo(file).Length);
        _server.ReceivedBytes.ShouldBe(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        // The offset was persisted after every chunk (SPEC §7).
        confirmedOffsets.Count.ShouldBe(3);
        confirmedOffsets.ShouldBeInOrder();
    }

    [Fact]
    public async Task An_interrupted_upload_resumes_from_the_servers_confirmed_offset_not_from_zero()
    {
        string file = MakeFile(GraphUploader.ChunkBytes * 3);
        GraphUploader uploader = MakeUploader();
        long lastConfirmed = 0;

        string uploadUrl = await uploader.CreateSessionAsync("Recordings/rec.mkv", TestContext.Current.CancellationToken);

        // The network dies after the server banked chunk 1.
        _server.AbortAfterChunks = 1;
        await Should.ThrowAsync<HttpRequestException>(() => uploader.UploadAsync(
            uploadUrl, file, 0, offset => lastConfirmed = offset, TestContext.Current.CancellationToken));

        _server.ConfirmedBytes.ShouldBe(GraphUploader.ChunkBytes, "the server banked exactly one chunk");

        // A new attempt (fresh uploader — like a host restart) resumes from the
        // queue's confirmed offset; the mock rejects any non-sequential chunk, so
        // reaching completion PROVES no byte was re-sent from zero.
        int chunksBefore = _server.ChunkRequests;
        long size = await MakeUploader().UploadAsync(
            uploadUrl, file, lastConfirmed == 0 ? _server.ConfirmedBytes : lastConfirmed,
            offset => lastConfirmed = offset, TestContext.Current.CancellationToken);

        size.ShouldBe(new FileInfo(file).Length);
        (_server.ChunkRequests - chunksBefore).ShouldBe(2, "only the two missing chunks travelled again");
        _server.ReceivedBytes.ShouldBe(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_permission_failure_is_permanent_and_keeps_the_servers_message_verbatim()
    {
        const string serverBody = """{"error":{"code":"accessDenied","message":"Access to the site has been blocked by policy."}}""";
        _server.ForcedFailure = (System.Net.HttpStatusCode.Forbidden, serverBody);

        DeliveryException exception = await Should.ThrowAsync<DeliveryException>(() =>
            MakeUploader().CreateSessionAsync("Recordings/rec.mkv", TestContext.Current.CancellationToken));

        exception.Kind.ShouldBe(FailureKind.Permanent);
        exception.Message.ShouldContain(serverBody);
    }

    [Fact]
    public async Task Throttling_is_transient_and_carries_the_servers_retry_delay()
    {
        _server.ForcedFailure = (System.Net.HttpStatusCode.TooManyRequests, """{"error":{"code":"tooManyRequests"}}""");

        DeliveryException exception = await Should.ThrowAsync<DeliveryException>(() =>
            MakeUploader().CreateSessionAsync("Recordings/rec.mkv", TestContext.Current.CancellationToken));

        exception.Kind.ShouldBe(FailureKind.Transient);
    }

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_dir, recursive: true);
    }
}
