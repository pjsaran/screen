using Shouldly;

namespace Captr.Integration.Tests;

/// <summary>
/// The published payload (<c>publish/</c>) — the real `captr.exe` and host that a
/// scheduled task or a user runs — for the tests that drive it, refusing to hand out
/// one that is older than the source.
/// </summary>
/// <remarks>
/// Five test classes each had their own copy of "find publish/", and none of them
/// checked its age. Four are in the Gpu category, which `build.ps1 -Full` ran BEFORE
/// its publish step — so they drove whatever <c>publish/</c> an earlier run had left
/// behind, days old, and passed or failed on code that no longer existed. A test that
/// silently tests the wrong build is worse than no test.
/// </remarks>
internal static class PublishedPayload
{
    public static string Directory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Captr.slnx")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull("could not locate the repo root");
        string publish = Path.Combine(root.FullName, "publish");
        string marker = Path.Combine(publish, "Captr.Core.dll");
        File.Exists(Path.Combine(publish, "captr.exe")).ShouldBeTrue(
            $"No published payload at {publish}. Run: pwsh build/build.ps1 -Publish");

        DateTime publishedUtc = File.GetLastWriteTimeUtc(marker);
        FileInfo? newer = new DirectoryInfo(Path.Combine(root.FullName, "src"))
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => file.Extension is ".cs" or ".xaml" or ".csproj" or ".txt"
                           && !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => file.LastWriteTimeUtc > publishedUtc)
            .MaxBy(file => file.LastWriteTimeUtc);

        newer.ShouldBeNull(
            $"publish/ was built at {publishedUtc.ToLocalTime():yyyy-MM-dd HH:mm} and {newer?.FullName} has changed since, " +
            "so these tests would exercise an old build. Run: pwsh build/build.ps1 -Publish");

        return publish;
    }

    public static string Cli() => Path.Combine(Directory(), "captr.exe");
}
