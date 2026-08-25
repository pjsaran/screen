using Captr.Core.Transfers;

using Shouldly;

namespace Captr.Core.Tests.Transfers;

/// <summary>
/// The bound on a single transfer attempt. Without it, a share that stops responding
/// mid-copy leaves a transfer showing "Sending" forever with nothing to rescue it.
/// </summary>
public class TransferWorkerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-worker-").FullName;

    private string WriteFileOfSize(long bytes)
    {
        string path = Path.Combine(_dir, $"file-{bytes}.bin");
        using FileStream stream = File.Create(path);
        stream.SetLength(bytes);
        return path;
    }

    [Fact]
    public void A_small_file_still_gets_a_generous_floor()
    {
        // Connecting, authenticating, and a slow first byte all happen before a single
        // byte of a tiny file moves, so the deadline can never be proportional alone.
        TransferWorker.AttemptTimeoutFor(WriteFileOfSize(1_000))
            .ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_large_file_gets_proportionally_longer()
    {
        // 1 GB at the assumed floor of 1 MB/s is about 1000 seconds. A fixed deadline
        // would cut off a legitimate transfer of a long recording.
        TimeSpan timeout = TransferWorker.AttemptTimeoutFor(WriteFileOfSize(1_000_000_000));

        timeout.ShouldBeGreaterThan(TimeSpan.FromMinutes(15));
        timeout.ShouldBeLessThan(TimeSpan.FromHours(1));
    }

    [Fact]
    public void A_file_that_cannot_be_measured_falls_back_to_the_floor()
    {
        // A missing file is a permanent failure handled elsewhere; deriving a deadline
        // must not be what throws.
        TransferWorker.AttemptTimeoutFor(Path.Combine(_dir, "does-not-exist.bin"))
            .ShouldBe(TimeSpan.FromMinutes(5));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
