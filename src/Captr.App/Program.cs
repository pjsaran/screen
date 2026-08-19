namespace Captr.App;

/// <summary>
/// Entry point for Captr.App.exe and the role switch mandated by SPEC §4: one executable,
/// two roles. Run bare, it starts the WPF desktop UI; run with <c>--host</c>, it starts
/// the headless recording host. This class owns nothing else — if it fails, the process
/// simply never starts.
/// </summary>
public static class Program
{
    /// <summary>The switch that selects the headless recording-host role.</summary>
    public const string HostSwitch = "--host";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(HostSwitch, StringComparer.OrdinalIgnoreCase))
        {
            return HostEntryPoint.Run(args);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
