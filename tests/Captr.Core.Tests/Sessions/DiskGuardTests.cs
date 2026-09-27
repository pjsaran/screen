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

    [Fact]
    public void The_ballast_is_not_counted_twice_while_it_sits_on_the_disk()
    {
        // While the ballast file exists, the free space reported by the volume has
        // already had it taken out. Subtracting it again stopped every recording
        // half a gigabyte early.
        var guard = new DiskGuard(_dir, OneGbPerHour);
        guard.ReserveBallast();

        guard.MinutesRemaining(freeBytes: 2_000_000_000).ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Room_to_join_the_segments_is_kept_back_so_the_recording_can_be_finalised()
    {
        // Finalising writes the joined file beside the segments: as many bytes again
        // as have been recorded. Ignoring that meant a recording stopped for low disk
        // had no room left for its own finalisation.
        var guard = new DiskGuard(_dir, OneGbPerHour);
        guard.ReserveBallast();

        DiskVerdict verdict = guard.Check(freeBytes: 3_000_000_000, recordedBytes: 2_950_000_000);

        verdict.State.ShouldBe(DiskState.Critical, "3 GB free with 2.95 GB to join leaves three minutes");
    }

    [Fact]
    public void Recorded_bytes_count_the_segments_and_nothing_else()
    {
        File.WriteAllBytes(Path.Combine(_dir, "seg-g01-000000.mkv"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_dir, "seg-g01-000500.mkv"), new byte[500]);
        File.WriteAllBytes(Path.Combine(_dir, "seg-g01-000000.mkv.repaired.mkv"), new byte[9999]);
        File.WriteAllBytes(Path.Combine(_dir, "journal.ndjson"), new byte[9999]);

        new DiskGuard(_dir, OneGbPerHour).RecordedBytes().ShouldBe(1500);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
