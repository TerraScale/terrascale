using System.Globalization;

namespace TerraScale.Dashboard.E2E
{
    // Synthetic providers exist only in this test executable, including cold-process journeys.
    // Runs from TestExecutableEntryPoint.Main, so the host is awaited like any other program and never blocks a module initializer.
    internal static class DashboardFixtureEntryPoint
    {
        // Port 0 asks the OS for a free port at bind time; the child announces the bound origin.
        internal static async Task<int> RunAsync(string[] args, int marker)
        {
            if (marker + 2 >= args.Length ||
                !int.TryParse(args[marker + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 0 or > 65535)
            {
                return 2;
            }

            return await PublicDemo.RunAsync(args[marker + 1], port);
        }
    }
}
