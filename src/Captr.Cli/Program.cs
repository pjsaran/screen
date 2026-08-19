namespace Captr.Cli;

/// <summary>
/// Entry point for the "captr" command line. Owns nothing but process startup:
/// it hands the raw arguments to the command tree in Captr.Core and returns its
/// exit code. If this fails, scripted and scheduled recording is broken.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Placeholder until WP8 wires the System.CommandLine tree from Captr.Core.
        await Console.Out.WriteLineAsync("captr (scaffold) — commands arrive in WP8.");
        return 0;
    }
}
