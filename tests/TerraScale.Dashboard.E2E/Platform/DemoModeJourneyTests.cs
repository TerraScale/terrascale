using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Platform
{
    // Synthetic subprocesses use the test executable; the production program always requires real providers.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DemoModeJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments(null)]
        [Arguments("true")]
        public async Task ProductionStartupAlwaysRequiresItsDeliveredCredentials(string? demo)
        {
            // Development reaches the loopback TerraBase origin, so the identity check is what stops it.
            using var process = Start(demo, FreePort(), "--environment", "Development");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                await Assert.That(process.ExitCode).IsNotEqualTo(0).Because("No demo fallback: the real host stops without its identity.");
                var log = await output + await error;
                await Assert.That(log).Contains("is not set");
                await Assert.That(log).DoesNotContain("DEMO (synthetic data)");
            }
            finally
            {
                Stop(process);
            }
        }

        // Port 0: the child binds an ephemeral port and announces it, so nothing is reserved before launch.
        private FixtureProcess StartFixture()
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = fixture.Root, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(DemoModeJourneyTests).Assembly.GetName().Name + ".dll"));
            start.ArgumentList.Add("--dashboard-fixture-host");
            start.ArgumentList.Add(Path.Combine(fixture.Root, "src", "TerraScale.Dashboard"));
            start.ArgumentList.Add("0");
            return FixtureProcess.Start(start);
        }

        private Process Start(string? demo, int port, params string[] arguments)
        {
            var project = Path.Combine(fixture.Root, "src", "TerraScale.Dashboard");
            var application = Path.Combine(project, "bin", "Release", "net11.0", "TerraScale.Dashboard.dll");
            if (!File.Exists(application))
            {
                throw new FileNotFoundException("Build the Release dashboard with the E2E suite.", application);
            }

            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = project, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(application);
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TS_", StringComparison.Ordinal) ||
                         name.StartsWith("STRIPE_", StringComparison.Ordinal) ||
                         name.StartsWith("ASPNETCORE_", StringComparison.Ordinal) || name == "DEMO").ToArray())
            {
                start.Environment.Remove(name);
            }

            if (demo is not null)
            {
                start.Environment["DEMO"] = demo;
            }

            start.Environment["ASPNETCORE_HTTP_PORTS"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Process.Start(start) ?? throw new IOException("Could not launch the dashboard program.");
        }

        private static async Task<JsonElement> ReadyAsync(HttpClient client, Process process)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (process.HasExited)
                {
                    throw new IOException("The demo exited before readiness.");
                }

                try
                {
                    using var response = await client.GetAsync("/health/ready");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return JsonElement.Parse(await response.Content.ReadAsStringAsync());
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(500);
            }
            throw new TimeoutException("The demo did not become ready.");
        }

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        private static void Stop(Process process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(5000);
        }
    }
}
