using System.Text;

using Shouldly;

namespace Captr.Integration.Tests.Build;

/// <summary>
/// <c>build/tools/normalise-line-endings.ps1</c> fixes the files we own and leaves
/// everything else alone.
/// </summary>
/// <remarks>
/// Its exclusion pattern was written as <c>[\/]</c>, which in a .NET regular
/// expression is a character class holding only <c>/</c>. Windows paths use
/// <c>\</c>, so nothing was ever excluded: every run walked and rewrote build output,
/// the fetched tools, and the contents of <c>.git</c>.
/// </remarks>
[Trait("Category", "Os")]
public sealed class NormaliseLineEndingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("captr-normalise-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Plant(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    [Fact]
    public async Task Source_files_are_fixed_and_build_output_tools_and_git_are_left_alone()
    {
        string source = Plant(@"src\Thing.cs", "class A\n{\n}\n");
        string[] untouchable =
        [
            Plant(@"src\obj\Release\Generated.cs", "lf\n"),
            Plant(@"src\bin\Release\app.json", "lf\n"),
            Plant(@"publish\Captr.App.xml", "lf\n"),
            Plant(@"artifacts\release.json", "lf\n"),
            Plant(@"tools\ffmpeg\capabilities.json", "lf\n"),
            Plant(@".git\description.txt", "lf\n"),
        ];

        (int exitCode, string output) = await BuildScriptHarness.RunAsync(
            BuildScriptHarness.Script(@"build\tools\normalise-line-endings.ps1"), ["-Root", _root]);

        exitCode.ShouldBe(0, output);
        (await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken)).ShouldBe("class A\r\n{\r\n}\r\n");
        foreach (string path in untouchable)
        {
            (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe("lf\n", $"{path} is not ours to rewrite");
        }
    }
}
