using System.Runtime.CompilerServices;

namespace TerraScale.Dashboard.E2E;

// Synthetic providers exist only in this test executable, including cold-process journeys.
internal static class DashboardFixtureEntryPoint
{
    [ModuleInitializer]
    internal static void DispatchTestProcess()
    {
        var args = Environment.GetCommandLineArgs();
        var marker = Array.IndexOf(args, "--dashboard-fixture-host");
        if (marker < 0) return;
        if (marker + 2 >= args.Length) Environment.Exit(2);
        var port = int.Parse(args[marker + 2], System.Globalization.CultureInfo.InvariantCulture);
        Environment.Exit(PublicDemo.RunAsync(args[marker + 1], port).GetAwaiter().GetResult());
    }
}
