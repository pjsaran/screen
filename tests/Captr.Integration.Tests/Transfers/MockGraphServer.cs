using System.Globalization;
using System.Net;
using System.Text;

namespace Captr.Integration.Tests.Transfers;

/// <summary>
/// A local HTTP server speaking just enough of Microsoft Graph's resumable-upload
/// protocol to prove SPEC §7's requirements: createUploadSession, sequential
/// Content-Range chunks answered with 202 + nextExpectedRanges, a final 201 with the
/// item size, session status via GET, and scriptable failures (aborts, 403s,
/// throttling). Runs on HttpListener — no extra dependencies.
/// </summary>
public sealed class MockGraphServer : IDisposable
{
    private readonly HttpListener _listener;
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

    /// <summary>The URL path of the last createUploadSession request — what proves
    /// the client addressed the configured drive id, not a made-up alias.</summary>
    public string? LastSessionPath { get; private set; }

    /// <summary>When set, the server hard-aborts the connection after accepting this
    /// many chunks — the "network died mid-upload" script.</summary>
    public int? AbortAfterChunks { get; set; }

    /// <summary>When set, every request is answered with this status and body —
    /// the permission-failure script.</summary>
    public (HttpStatusCode Status, string Body)? ForcedFailure { get; set; }

    /// <summary>Upload sessions created so far.</summary>
    public int SessionsCreated { get; private set; }

    /// <summary>When set, the FINAL chunk is stored and the item completed, but the
    /// connection drops before the 201 is sent — the "success whose reply was lost"
    /// script.</summary>
    public bool LoseFinalReply { get; set; }

    /// <summary>
    /// Expires the current upload session, as Graph does to an idle one: its URL now
    /// answers 404, and whatever it had received is discarded — a new session starts
    /// from the first byte.
    /// </summary>
    public void ExpireCurrentSession()
    {
        lock (_gate)
        {
            _expiredSessions.Add(_currentSession);
            _received.SetLength(0);
        }
    }

    private readonly HashSet<int> _expiredSessions = [];
    private readonly Dictionary<string, long> _completedItems = new(StringComparer.OrdinalIgnoreCase);
    private int _currentSession;
    private string _currentItemPath = "";

    public MockGraphServer()
    {
        // A random port can already be taken (by another test's listener still
        // closing, or anything else on the machine); try a few before giving up
        // rather than failing a test on a coin toss.
        // A listener whose Start fails disposes itself, so each try gets a new one.
        for (int attempt = 1; ; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            BaseUrl = new Uri($"http://127.0.0.1:{port}/graph/");
            var listener = new HttpListener();
            listener.Prefixes.Add(BaseUrl.ToString());
            try
            {
                listener.Start();
                _listener = listener;
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
            }
        }

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
            // Kept so tests can prove the client addressed the RIGHT drive — the
            // real Graph rejects anything but an actual drive id here.
            LastSessionPath = request.Url.AbsolutePath;
            int session;
            lock (_gate)
            {
                session = ++_currentSession;
                SessionsCreated++;
                _received.SetLength(0);
                _currentItemPath = ItemPathOf(request.Url.AbsolutePath.Replace(":/createUploadSession", "", StringComparison.Ordinal));
            }

            await WriteJsonAsync(response, 200,
                $$"""{"uploadUrl":"{{BaseUrl}}upload-session/{{session.ToString(CultureInfo.InvariantCulture)}}","expirationDateTime":"2030-01-01T00:00:00Z"}""")
                .ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && request.Url!.AbsolutePath.Contains("/root:/", StringComparison.Ordinal))
        {
            // Item lookup: what a client asks after losing a completion reply.
            long? size;
            lock (_gate)
            {
                size = _completedItems.TryGetValue(ItemPathOf(request.Url.AbsolutePath), out long found) ? found : null;
            }

            if (size is { } bytes)
            {
                await WriteJsonAsync(response, 200,
                    $$"""{"id":"item1","size":{{bytes.ToString(CultureInfo.InvariantCulture)}}}""").ConfigureAwait(false);
            }
            else
            {
                response.StatusCode = 404;
                response.Close();
            }

            return;
        }

        if (request.Url!.AbsolutePath.Contains("upload-session", StringComparison.Ordinal))
        {
            int session = int.Parse(request.Url.Segments[^1], CultureInfo.InvariantCulture);
            bool gone;
            lock (_gate)
            {
                gone = _expiredSessions.Contains(session);
            }

            if (gone)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

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
            lock (_gate)
            {
                // A completed session is gone, exactly as in Graph: its URL now 404s.
                _completedItems[_currentItemPath] = ConfirmedBytes;
                _expiredSessions.Add(_currentSession);
            }

            if (LoseFinalReply)
            {
                LoseFinalReply = false;
                response.Abort();
                return;
            }

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

    /// <summary>"/graph/drives/{id}/root:/folder/name.mkv" → "folder/name.mkv", unescaped.</summary>
    private static string ItemPathOf(string absolutePath) =>
        Uri.UnescapeDataString(absolutePath[(absolutePath.IndexOf("/root:/", StringComparison.Ordinal) + "/root:/".Length)..]);

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
