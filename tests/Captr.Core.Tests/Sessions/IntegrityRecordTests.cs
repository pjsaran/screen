using System.Security.Cryptography;

using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Core.Tests.Sessions;

/// <summary>
/// The integrity record and its verification pass (SPEC §6: "a later verification
/// pass can prove nothing has been altered on disk").
/// </summary>
public class IntegrityRecordTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-integrity-").FullName;

    /// <summary>Writes a file and returns its real SHA-256, so the record under test
    /// describes bytes that actually exist.</summary>
    private (string FileName, long Size, string Hash) WriteFile(string fileName, string content)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        using FileStream stream = File.OpenRead(path);
        return (fileName, new FileInfo(path).Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private IntegrityRecord WriteRecordFor(params (string FileName, long Size, string Hash)[] outputs)
    {
        var record = new IntegrityRecord
        {
            SessionId = Guid.NewGuid(),
            FinalizedUtc = DateTimeOffset.UtcNow,
            Segments = [],
            Outputs = [.. outputs.Select(o => new HashedFile(o.FileName, o.Size, o.Hash))],
            Coverage = new CoverageReport(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                TimeSpan.Zero, TimeSpan.Zero, []),
            RepairedSegments = 0,
            Notes = [],
        };
        record.Write(_dir);
        return record;
    }

    [Fact]
    public async Task An_untouched_recording_verifies_clean()
    {
        IntegrityRecord record = WriteRecordFor(WriteFile("joined-g01.mkv", "the footage"));

        IReadOnlyList<string> problems = await record.VerifyAsync(_dir, TestContext.Current.CancellationToken);

        problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task Altered_content_of_the_same_length_is_caught_by_the_hash()
    {
        // The size is deliberately unchanged: only the hash can catch this, which is
        // the whole reason the record stores one.
        IntegrityRecord record = WriteRecordFor(WriteFile("joined-g01.mkv", "the footage"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "joined-g01.mkv"), "the f00tage", TestContext.Current.CancellationToken);

        IReadOnlyList<string> problems = await record.VerifyAsync(_dir, TestContext.Current.CancellationToken);

        problems.ShouldHaveSingleItem().ShouldContain("content altered");
    }

    [Fact]
    public async Task A_truncated_file_is_reported_as_a_size_change()
    {
        IntegrityRecord record = WriteRecordFor(WriteFile("joined-g01.mkv", "the whole footage"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "joined-g01.mkv"), "the whole", TestContext.Current.CancellationToken);

        IReadOnlyList<string> problems = await record.VerifyAsync(_dir, TestContext.Current.CancellationToken);

        problems.ShouldHaveSingleItem().ShouldContain("size changed");
    }

    [Fact]
    public async Task A_deleted_file_is_reported_as_missing()
    {
        IntegrityRecord record = WriteRecordFor(WriteFile("joined-g01.mkv", "the footage"));
        File.Delete(Path.Combine(_dir, "joined-g01.mkv"));

        IReadOnlyList<string> problems = await record.VerifyAsync(_dir, TestContext.Current.CancellationToken);

        problems.ShouldHaveSingleItem().ShouldContain("missing");
    }

    /// <summary>
    /// The regression that mattered: finalisation hashes <c>joined-gNN.mkv</c>, and
    /// the host then renames it to the user's naming pattern. Until the record was
    /// taught about the rename, verification reported EVERY finalised recording as
    /// "missing" — the feature was completely broken in normal use.
    /// </summary>
    [Fact]
    public async Task A_renamed_output_still_verifies_once_the_rename_is_recorded()
    {
        IntegrityRecord record = WriteRecordFor(WriteFile("joined-g01.mkv", "the footage"));

        File.Move(Path.Combine(_dir, "joined-g01.mkv"), Path.Combine(_dir, "MACHINE 2026-08-20 10-00-00.mkv"));

        // Before the record is updated, verification correctly says the file is gone.
        (await record.VerifyAsync(_dir, TestContext.Current.CancellationToken))
            .ShouldHaveSingleItem().ShouldContain("missing");

        IntegrityRecord.RecordOutputRename(_dir, "joined-g01.mkv", "MACHINE 2026-08-20 10-00-00.mkv")
            .ShouldBeTrue();

        IntegrityRecord reloaded = IntegrityRecord.ReadOrNull(_dir).ShouldNotBeNull();
        (await reloaded.VerifyAsync(_dir, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Recording_a_rename_that_matches_no_output_changes_nothing_and_says_so()
    {
        WriteRecordFor(WriteFile("joined-g01.mkv", "the footage"));

        IntegrityRecord.RecordOutputRename(_dir, "not-an-output.mkv", "whatever.mkv").ShouldBeFalse();

        IntegrityRecord reloaded = IntegrityRecord.ReadOrNull(_dir).ShouldNotBeNull();
        reloaded.Outputs.ShouldHaveSingleItem().FileName.ShouldBe("joined-g01.mkv");
    }

    [Fact]
    public void A_rename_keeps_the_hash_and_size_because_renaming_moves_no_bytes()
    {
        (string _, long size, string hash) = WriteFile("joined-g01.mkv", "the footage");
        WriteRecordFor(("joined-g01.mkv", size, hash));

        IntegrityRecord.RecordOutputRename(_dir, "joined-g01.mkv", "renamed.mkv").ShouldBeTrue();

        HashedFile output = IntegrityRecord.ReadOrNull(_dir).ShouldNotBeNull().Outputs.ShouldHaveSingleItem();
        output.FileName.ShouldBe("renamed.mkv");
        output.SizeBytes.ShouldBe(size);
        output.Sha256.ShouldBe(hash);
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
