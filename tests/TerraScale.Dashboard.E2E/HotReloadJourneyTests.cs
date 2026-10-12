using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class HotReloadJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task CSharpAndCssChangesReachTheOpenTailscaleBrowserAndRestore()
    {
        var hash = Convert.ToUInt32(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Root)))[..8], 16);
        var origin = new Uri($"http://{Environment.MachineName}.tail057f.ts.net:{7500 + hash % 1000}");
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = fixture.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("scripts/demo.sh");
        using var host = Process.Start(start) ?? throw new IOException("Could not start the test-only development watcher.");
        var output = host.StandardOutput.ReadToEndAsync();
        var errors = host.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (true)
            {
                if (host.HasExited)
                    throw new IOException("Demo watcher ended before readiness: " + await output + await errors);
                try
                {
                    using var ready = await client.GetAsync(new Uri(origin, "/readyz"), deadline.Token);
                    if (ready.StatusCode == HttpStatusCode.OK) break;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(250, deadline.Token);
            }
            await BrowserJourneys.RunAsync(fixture.Root, "hot-reload.mjs", origin);
        }
        finally
        {
            if (!host.HasExited)
            {
                var stop = new ProcessStartInfo("kill");
                stop.ArgumentList.Add("-TERM");
                stop.ArgumentList.Add(host.Id.ToString());
                using var signal = Process.Start(stop) ?? throw new IOException("Could not stop the test-only development watcher.");
                await signal.WaitForExitAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await host.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync(); }
            }
            Console.WriteLine(await output + await errors);
        }
    }
}
