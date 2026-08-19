using System.Runtime.CompilerServices;

namespace Captr.Core.Tests.Encoders;

/// <summary>
/// Golden-file plumbing for the argument-builder tests (SPEC §14: "golden-file
/// assertions on generated encoder arguments"). A golden file holds the expected
/// argument vector, one argument per line. On mismatch the test fails with a diff;
/// setting the environment variable <c>CAPTR_ACCEPT_GOLDEN=1</c> regenerates the
/// files IN THE SOURCE TREE so the change lands in review as a readable diff.
/// Never hand-edit a golden file.
/// </summary>
internal static class GoldenFile
{
    public static void Assert(string caseName, IReadOnlyList<string> actualArguments)
    {
        string path = Path.Combine(GoldenDirectory(), caseName + ".args.txt");
        string actual = string.Join("\n", actualArguments) + "\n";

        if (Environment.GetEnvironmentVariable("CAPTR_ACCEPT_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        if (!File.Exists(path))
        {
            throw new Xunit.Sdk.XunitException(
                $"Golden file missing: {path}\n" +
                "If this is a new case, generate it with CAPTR_ACCEPT_GOLDEN=1 and review the result.\n" +
                $"Actual arguments were:\n{actual}");
        }

        string expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (expected != actual)
        {
            throw new Xunit.Sdk.XunitException(
                $"Generated arguments differ from golden file {caseName}.args.txt.\n" +
                "If the change is INTENDED, regenerate with CAPTR_ACCEPT_GOLDEN=1 and review the diff.\n" +
                $"--- expected ---\n{expected}\n--- actual ---\n{actual}");
        }
    }

    /// <summary>The Golden directory in the SOURCE tree, located relative to this
    /// file via CallerFilePath — so regeneration writes where git can see it.</summary>
    private static string GoldenDirectory([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "Golden");
}
