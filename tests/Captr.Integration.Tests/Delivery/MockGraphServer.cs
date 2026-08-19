using System.Globalization;
using System.Net;
using System.Text;

namespace Captr.Integration.Tests.Delivery;

/// <summary>
/// A local HTTP server speaking just enough of Microsoft Graph's resumable-upload
/// protocol to prove SPEC §7's requirements: createUploadSession, sequential
/// Content-Range chunks answered with 202 + nextExpectedRanges, a final 201 with the
/// item size, session status via GET, and scriptable failures (aborts, 403s,
/// throttling). Runs on HttpListener — no extra dependencies.
/// </summary>
public sealed class MockGraphServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly MemoryStream _received = new();
    private readonly Lock _gate = new();
    private readonly Task _serveLoop;

    /// <summary>Base URL for the uploader's HttpClient.</summary>
    public Uri BaseUrl { get; }

    /// <summary>Bytes accepted so far (the server's truth for nextExpectedRanges).</summary>
    public long ConfirmedBytes
    {
        get
        {
            lock (_gate)
            {
                return _received.Length;
            }
        }
    }

    /// <summary>Everything the server accepted, for end-to-end content comparison.</summary>
    public byte[] ReceivedBytes
    {
        get
        {
            lock (_gate)
            {
                return _received.ToArray();
            }
        }
    }

    /// <summary>Chunk uploads served so far.</summary>
    public int ChunkRequests { get; private set; }

    /// <summary>When set, the server hard-aborts the connection after accepting this
    /// many chunks — the "network died mid-upload" script.</summary>
    public int? AbortAfterChunks { get; set; }

    /// <summary>When set, every request is answered with this status and body —
    /// the permission-failure script.</summary>
    public (HttpStatusCode Status, string Body)? ForcedFailure { get; set; }

    public MockGraphServer()
    {
        int port = Random.Shared.Next(20000, 60000);
        BaseUrl = new Uri($"http://127.0.0.1:{port}/graph/");
        _listener.Prefixes.Add(BaseUrl.ToString());
        _listener.Start();
        _serveLoop = Task.Run(ServeLoopAsync);
    }

    private async Task ServeLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                await HandleAsync(context).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException)
            {
                // A scripted abort or a raced shutdown — part of the test.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        if (ForcedFailure is { } failure)
        {
            response.StatusCode = (int)failure.Status;
            byte[] body = Encoding.UTF8.GetBytes(failure.Body);
            await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            response.Close();
            return;
        }

        if (request.HttpMethod == "POST" && request.Url!.AbsolutePath.EndsWith("createUploadSession", StringComparison.Ordinal))
        {
            await WriteJsonAsync(response, 200,
                $$"""{"uploadUrl":"{{BaseUrl}}upload-session/1","expirationDateTime":"2030-01-01T00:00:00Z"}""")
                .ConfigureAwait(false);
            return;
        }

        if (request.Url!.AbsolutePath.Contains("upload-session", StringComparison.Ordinal))
        {
            if (request.HttpMethod == "GET")
            {
                // Session status: where the server actually got to.
                await WriteJsonAsync(response, 200,
                    $$"""{"nextExpectedRanges":["{{ConfirmedBytes.ToString(CultureInfo.InvariantCulture)}}-"]}""")
                    .ConfigureAwait(false);
                return;
            }

            await HandleChunkAsync(request, response).ConfigureAwait(false);
            return;
        }

        response.StatusCode = 404;
        response.Close();
    }

    private async Task HandleChunkAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        // Content-Range: bytes 0-2621439/5000000
        string range = request.Headers["Content-Range"]!;
        string[] parts = range.Replace("bytes ", "", StringComparison.Ordinal).Split('/', '-');
        long from = long.Parse(parts[0], CultureInfo.InvariantCulture);
        long total = long.Parse(parts[2], CultureInfo.InvariantCulture);

        using var chunk = new MemoryStream();
        await request.InputStream.CopyToAsync(chunk).ConfigureAwait(false);

        lock (_gate)
        {
            // Sequential-chunk contract: the chunk must start where we ended.
            if (from != _received.Length)
            {
                throw new InvalidOperationException(
                    $"Non-sequential chunk: got offset {from}, expected {_received.Length}.");
            }

            _received.Write(chunk.ToArray());
            ChunkRequests++;
        }

        if (AbortAfterChunks is { } limit && ChunkRequests >= limit)
        {
            AbortAfterChunks = null;
            response.Abort(); // Hard connection drop, mid-upload.
            return;
        }

        if (ConfirmedBytes >= total)
        {
            await WriteJsonAsync(response, 201,
                $$"""{"id":"item1","size":{{ConfirmedBytes.ToString(CultureInfo.InvariantCulture)}}}""")
                .ConfigureAwait(false);
        }
        else
        {
            await WriteJsonAsync(response, 202,
                $$"""{"nextExpectedRanges":["{{ConfirmedBytes.ToString(CultureInfo.InvariantCulture)}}-"]}""")
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, string json)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        byte[] body = Encoding.UTF8.GetBytes(json);
        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        response.Close();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _listener.Close();
        _shutdown.Dispose();
    }
}
