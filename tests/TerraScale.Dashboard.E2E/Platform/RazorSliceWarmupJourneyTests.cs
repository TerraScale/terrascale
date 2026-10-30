using System.Diagnostics;
using System.Net;

namespace TerraScale.Dashboard.E2E.Platform
{
    // RazorSlices' per-slice initializers share state that is not thread-safe, so the program initializes every slice
    // before it serves. Each round is a cold process whose first page loads all arrive at once: none may fail or leave
    // a slice broken for the requests after it.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class RazorSliceWarmupJourneyTests(DashboardRootFixture fixture)
    {
        private static readonly string[] Pages = ["/", "/overview", "/databases", "/analytics", "/usage", "/billing", "/api-keys",
            "/audit", "/imports", "/notifications", "/onboarding", "/organization", "/organization?tab=billing", "/organizations",
            "/profile", "/support", "/invitations", "/project", "/missing-page"];

        [Test]
        public async Task ColdConcurrentFirstRendersNeverPoisonASlice()
        {
            for (var round = 0; round < 3; round++)
            {
                await using var host = Start();
                string log;
                try
                {
                    var origin = await host.WaitForOriginAsync();
                    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
                    { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
                    await ReadyAsync(client, host.Process);
                    using (var login = await HttpJourney.LoginAsync(client))
                    {
                        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                    }

                    for (var pass = 0; pass < 2; pass++)
                    {
                        // Pass 0 is the cold burst; pass 1 proves no slice type was left broken by it.
                        var statuses = await Task.WhenAll(Pages.SelectMany(page => Enumerable.Repeat(page, 4)).Select(async page =>
                        {
                            using var response = await client.GetAsync(page);
                            return (page, (int)response.StatusCode);
                        }));
                        await Assert.That(statuses.Where(s => s.Item2 >= 500).Select(s => $"{s.page} {s.Item2}")).IsEmpty();
                    }
                }
                finally
                {
                    log = await host.StopAsync();
                }
                await Assert.That(log).DoesNotContain("TypeInitializationException");
            }
        }

        // Port 0: the child binds an ephemeral port and announces it, so nothing is reserved before launch.
        private FixtureProcess Start()
        {
            var project = Path.Combine(fixture.Root, "src", "TerraScale.Dashboard");
            var application = typeof(RazorSliceWarmupJourneyTests).Assembly.Location;
            if (!File.Exists(application))
            {
                throw new FileNotFoundException("Build the Release dashboard with the E2E suite.", application);
            }

            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = project, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(application);
            start.ArgumentList.Add("--dashboard-fixture-host");
            start.ArgumentList.Add(project);
            start.ArgumentList.Add("0");
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TS_", StringComparison.Ordinal) ||
                         name.StartsWith("STRIPE_", StringComparison.Ordinal) || name.StartsWith("ASPNETCORE_", StringComparison.Ordinal)).ToArray())
            {
                start.Environment.Remove(name);
            }

            start.Environment["ASPNETCORE_HTTP_PORTS"] = "0";
            return FixtureProcess.Start(start);
        }

        private static async Task ReadyAsync(HttpClient client, Process process)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (process.HasExited)
                {
                    throw new IOException("The dashboard exited before readiness.");
                }

                try
                {
                    using var response = await client.GetAsync("/health/ready");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(500);
            }
            throw new TimeoutException("The dashboard did not become ready.");
        }

    }
}
