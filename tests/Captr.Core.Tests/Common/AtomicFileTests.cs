using Captr.Core.Common;

using Shouldly;

namespace Captr.Core.Tests.Common;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-atomicfile-").FullName;

    [Fact]
    public void Write_creates_the_file_with_the_exact_content()
    {
        string path = Path.Combine(_dir, "a.json");
        AtomicFile.Write(path, "hello");
        File.ReadAllText(path).ShouldBe("hello");
    }

    [Fact]
    public void Write_replaces_existing_content_completely()
    {
        string path = Path.Combine(_dir, "a.json");
        AtomicFile.Write(path, "a much longer original content that should disappear");
        AtomicFile.Write(path, "short");
        File.ReadAllText(path).ShouldBe("short");
    }

    [Fact]
    public void Write_leaves_no_temp_file_behind()
    {
        string path = Path.Combine(_dir, "a.json");
        AtomicFile.Write(path, "x");
        Directory.GetFiles(_dir).ShouldBe([path]);
    }

    [Fact]
    public void ReadOrNull_returns_null_for_a_missing_file()
    {
        AtomicFile.ReadOrNull(Path.Combine(_dir, "missing.json")).ShouldBeNull();
    }

    // SPEC §6: "Pin the atomicity of that rename with a test rather than assuming the
    // framework's file-move overload provides it." A writer replaces the file as fast
    // as it can with two distinguishable payloads while a reader hammers reads; if the
    // rename were not atomic the reader would eventually observe a mixture or an
    // empty/partial file.
    [Fact]
    public async Task Concurrent_reader_never_observes_a_torn_file()
    {
        string path = Path.Combine(_dir, "heartbeat.json");
        string payloadA = "A" + new string('a', 4096) + "A";
        string payloadB = "B" + new string('b', 8192) + "B";
        AtomicFile.Write(path, payloadA);

        // Volatile flag rather than a CancellationTokenSource: the reader must stop
        // even when the writer FAILS (a faulted writer once left the reader spinning
        // forever — the hang was how this test first caught a real AtomicFile bug).
        bool writerDone = false;

        Task writer = Task.Run(
            () =>
            {
                try
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        AtomicFile.Write(path, i % 2 == 0 ? payloadB : payloadA);
                    }
                }
                finally
                {
                    Volatile.Write(ref writerDone, true);
                }
            },
            TestContext.Current.CancellationToken);

        Task reader = Task.Run(
            () =>
            {
                while (!Volatile.Read(ref writerDone))
                {
                    string? content = AtomicFile.ReadOrNull(path);
                    content.ShouldNotBeNull();
                    bool intact = content == payloadA || content == payloadB;
                    intact.ShouldBeTrue($"Torn read observed: length={content.Length}, first={content[0]}, last={content[^1]}");
                }
            },
            TestContext.Current.CancellationToken);

        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
