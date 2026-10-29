using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
[Explicit]
[Category("Performance")]
public sealed class PerformanceJourneyTests(DashboardRootFixture fixture)
{
    private const int Runs = 5;

    private const string Observers = """
        window.perf = { lcp: 0, cls: 0, longTasks: 0 };
        new PerformanceObserver(list => {
          for (const entry of list.getEntries()) window.perf.lcp = entry.startTime;
        }).observe({ type: 'largest-contentful-paint', buffered: true });
        new PerformanceObserver(list => {
          for (const entry of list.getEntries()) if (!entry.hadRecentInput) window.perf.cls += entry.value;
        }).observe({ type: 'layout-shift', buffered: true });
        new PerformanceObserver(list => {
          for (const entry of list.getEntries()) window.perf.longTasks += entry.duration;
        }).observe({ type: 'longtask', buffered: true });
        """;

    private const string Measure = """
        () => {
          const nav = performance.getEntriesByType('navigation')[0];
          return { ...window.perf, ttfb: nav.responseStart, domContentLoaded: nav.domContentLoadedEventEnd,
            bytes: nav.transferSize + performance.getEntriesByType('resource').reduce((n, r) => n + r.transferSize, 0) };
        }
        """;

    [Test]
    public async Task NativeBrowserMeasuresColdAndWarmDesktopAndThrottledMobileJourneys()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        var origin = scenario.Origin.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var errors = new ConcurrentQueue<string>();
        var results = new List<Dictionary<string, object>>();

        string? database;
        await using (var inventory = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true }))
        {
            var page = await inventory.NewPageAsync();
            await LoginAsync(page);
            var response = await page.GotoAsync(origin + "/databases");
            await Assert.That(response!.Status).IsEqualTo(200);
            database = await page.GetByRole(AriaRole.Link, new() { NameRegex = new(@"^core-primary\b") }).GetAttributeAsync("href");
            await Assert.That(database).Matches(@"^/databases/[0-9A-HJKMNP-TV-Z]{26}$");
        }
        foreach (var profile in new[] { "desktop", "mobile" })
            foreach (var path in new[] { "/", "/databases", database + "/sql", "/usage" })
                for (var run = 0; run < Runs; run++)
                    await MeasureAsync(profile, path, run);

        await Assert.That(errors.ToArray()).IsEmpty();
        await Assert.That(results.Count).IsEqualTo(80);
        var summary = results.GroupBy(row => row["profile"] + " " + row["path"] + " " + row["cache"]).Select(group =>
        {
            var row = new Dictionary<string, object> { ["key"] = group.Key };
            foreach (var metric in new[] { "lcp", "cls", "ttfb", "domContentLoaded", "bytes", "longTasks" })
            {
                var values = group.Select(r => (double)r[metric]).Order().ToArray();
                // p75 with half-up rounding, matching the former Math.round(v * 100) / 100.
                row[metric] = Math.Round(values[(int)Math.Ceiling(values.Length * 0.75) - 1] * 100, MidpointRounding.AwayFromZero) / 100;
            }
            return row;
        }).ToArray();
        var output = Path.Combine(BrowserAudit.Output(fixture.Root, ""), "performance.json");
        var json = JsonSerializer.Serialize(new { baseURL = origin, runs = Runs, summary, results }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(output, json + "\n");
        Console.WriteLine("PASS: 80 real cold/warm desktop/mobile measurements; " + output);

        async Task MeasureAsync(string profile, string path, int run)
        {
            await using var context = await browser.NewContextAsync(new()
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = profile == "mobile" ? new() { Width = 390, Height = 844 } : new() { Width = 1440, Height = 900 }
            });
            var page = await context.NewPageAsync();
            page.PageError += (_, error) => errors.Enqueue(error);
            await LoginAsync(page);
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Network.enable");
            await cdp.SendAsync("Network.clearBrowserCache");
            if (profile == "mobile")
            {
                await cdp.SendAsync("Emulation.setCPUThrottlingRate", new Dictionary<string, object> { ["rate"] = 4 });
                await cdp.SendAsync("Network.emulateNetworkConditions", new Dictionary<string, object>
                {
                    ["offline"] = false,
                    ["latency"] = 80,
                    ["downloadThroughput"] = 200000,
                    ["uploadThroughput"] = 100000
                });
            }
            await page.AddInitScriptAsync(Observers);
            foreach (var cache in new[] { "cold", "warm" })
            {
                var response = await page.GotoAsync(origin + path, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                await Assert.That(response!.Status).IsEqualTo(200).Because(profile + " " + path);
                await Assert.That(new Uri(page.Url).AbsolutePath).IsEqualTo(path);
                await page.EvaluateAsync("() => document.fonts.ready");
                await page.WaitForFunctionAsync("() => window.perf.lcp > 0", null, new() { Timeout = 15000 });
                await page.WaitForTimeoutAsync(500);
                // Playwright .NET tags returned objects with a "$id" reference marker; it is not a measurement.
                var measured = (await page.EvaluateAsync<JsonElement>(Measure)).EnumerateObject().Where(p => p.Name != "$id").ToDictionary(p => p.Name, p => p.Value.GetDouble());
                foreach (var (name, value) in measured)
                    await Assert.That(double.IsFinite(value) && value >= 0).IsTrue().Because(name + " must be an actual browser measurement");
                await Assert.That(measured["bytes"]).IsGreaterThan(0);
                var row = new Dictionary<string, object> { ["profile"] = profile, ["path"] = path, ["run"] = run, ["cache"] = cache };
                foreach (var (name, value) in measured) row[name] = value;
                results.Add(row);
            }
        }

        async Task LoginAsync(IPage page)
        {
            var response = await BrowserAudit.SignInAsync(page, scenario.Origin, email: "mario@terrascale.tech", password: "terrascale");
            await Assert.That(response!.Status).IsEqualTo(200);
            await Assert.That(BrowserAudit.WithoutWorkspace(page.Url)).IsEqualTo(origin + "/");
        }
    }
}
