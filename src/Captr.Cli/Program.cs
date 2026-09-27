using Captr.Core.Cli;

namespace Captr.Cli;

/// <summary>
/// Entry point for the "captr" command line. Owns nothing but process startup:
/// it hands the raw arguments to the command tree in Captr.Core and returns its
/// exit code. If this fails, scripted and scheduled recording is broken.
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args)
    {
        Captr.Core.Diagnostics.CrashReporter.Install("cli");
        return CliApplication.RunAsync(args);
    }
}
