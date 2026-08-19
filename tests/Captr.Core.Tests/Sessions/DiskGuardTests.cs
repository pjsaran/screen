using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Core.Tests.Sessions;

public class DiskGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-disk-").FullName;

    private const long OneGbPerHour = 1_000_000_000;

    [Fact]
    public void Preflight_passes_with_ample_space()
    {
        var guard = new DiskGuard(_dir, OneGbPerHour);

        guard.Preflight(freeBytes: 10_000_000_000).CanStart.ShouldBeTrue();
    }

    [Fact]
    public void Preflight_refuses_with_a_message_stating_what_is_needed()
    {
        var guard = new DiskGuard(_dir, OneGbPerHour);

        PreflightResult result = guard.Preflight(freeBytes: 200_000_000);

        result.CanStart.ShouldBeFalse();
        result.RefusalMessage.ShouldNotBeNull();
        result.RefusalMessage.ShouldContain("GB free");
        result.RefusalMessage.ShouldContain("is needed");
    }

    [Fact]
    public void Warnings_are_expressed_in_recording_time_not_bytes()
    {
        var guard = new DiskGuard(_dir, OneGbPerHour);

        // Ballast (0.54 GB) + ~0.25 GB usable ≈ 15 minutes at 1 GB/h → Warning band.
        DiskVerdict verdict = guard.Check(freeBytes: 800_000_000);

        verdict.State.ShouldBe(DiskState.Warning);
        verdict.RecordingTimeRemaining.ShouldBeGreaterThan(TimeSpan.FromMinutes(5));
        verdict.RecordingTimeRemaining.ShouldBeLessThan(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Nearly_full_disk_is_critical()
    {
        var guard = new DiskGuard(_dir, OneGbPerHour);

        guard.Check(freeBytes: DiskGuard.BallastBytes + 10_000_000).State.ShouldBe(DiskState.Critical);
    }

    [Fact]
    public void Plenty_of_space_is_ok()
    {
        var guard = new DiskGuard(_dir, OneGbPerHour);

        guard.Check(freeBytes: 50_000_000_000).State.ShouldBe(DiskState.Ok);
    }

    [Fact]
    public void Ballast_is_reserved_at_full_size_and_released_on_demand()
    {
        var guard = new DiskGuard(_dir, OneGbPerHour);

        guard.ReserveBallast();
        var ballast = new FileInfo(Path.Combine(_dir, "ballast.bin"));
        ballast.Exists.ShouldBeTrue();
        ballast.Length.ShouldBe(DiskGuard.BallastBytes);

        guard.ReleaseBallast();
        File.Exists(ballast.FullName).ShouldBeFalse();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
