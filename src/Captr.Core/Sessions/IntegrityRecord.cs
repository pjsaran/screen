using System.Text.Json;
using System.Text.Json.Serialization;

using Captr.Core.Common;

namespace Captr.Core.Sessions;

/// <summary>
/// The closed-out integrity record of a session (<c>integrity.json</c>): every
/// segment and output with its SHA-256, the honest coverage report, repair count,
/// and notes. Owns the proof SPEC §6 asks for — "a later verification pass can
/// prove nothing has been altered on disk". <see cref="VerifyAsync"/> is that pass.
/// </summary>
public sealed record IntegrityRecord
{
    public const string FileName = "integrity.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public required Guid SessionId { get; init; }
    public required DateTimeOffset FinalizedUtc { get; init; }
    public required IReadOnlyList<FinalizedSegment> Segments { get; init; }
    public required IReadOnlyList<HashedFile> Outputs { get; init; }
    public required CoverageReport Coverage { get; init; }
    public required int RepairedSegments { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Writes the record atomically into the working folder.</summary>
    public void Write(string workingFolder) =>
        AtomicFile.Write(
            Path.Combine(workingFolder, FileName),
            JsonSerializer.Serialize(this, SerializerOptions));

    /// <summary>Reads a session's record, or null when it was never finalised.</summary>
    public static IntegrityRecord? ReadOrNull(string workingFolder)
    {
        string? json = AtomicFile.ReadOrNull(Path.Combine(workingFolder, FileName));
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IntegrityRecord>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The verification pass: recomputes every hash on disk and compares. Any
    /// mismatch, missing file, or size change is reported — proving (or disproving)
    /// that nothing was altered since finalisation (SPEC §6).
    /// </summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(string workingFolder, CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        foreach ((string fileName, long expectedSize, string expectedHash) in
                 Segments.Select(s => (s.FileName, s.SizeBytes, s.Sha256))
                     .Concat(Outputs.Select(o => (o.FileName, o.SizeBytes, o.Sha256))))
        {
            string path = Path.Combine(workingFolder, fileName);
            if (!File.Exists(path))
            {
                problems.Add($"{fileName}: missing");
                continue;
            }

            long actualSize = new FileInfo(path).Length;
            if (actualSize != expectedSize)
            {
                problems.Add($"{fileName}: size changed ({expectedSize} → {actualSize} bytes)");
                continue;
            }

            string actualHash = await FinalizationPipeline.HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{fileName}: content altered (SHA-256 mismatch)");
            }
        }

        return problems;
    }
}
