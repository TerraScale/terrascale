using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using RazorSlices;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Shared.Usage;

namespace TerraScale.Dashboard.E2E.Features.Analytics;

public sealed class ChartBucketJourneyTests
{
    [Test]
    [Arguments("1h", 1, 3600)]
    [Arguments("1h", 60, 60)]
    [Arguments("24h", 24, 3600)]
    [Arguments("7d", 28, 21600)]
    [Arguments("7d", 168, 3600)]
    [Arguments("30d", 30, 86400)]
    [Arguments("30d", 720, 3600)]
    public async Task EveryPlottedBucketHasItsExactTimestampAndValue(string range, int count, int seconds)
    {
        var starts = Enumerable.Range(0, count).Select(i => new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i * seconds)).ToArray();
        var values = Enumerable.Range(0, count).Select(i => i == count - 1 ? 900_000_000_001L : 1001L + i).ToArray();
        var clock = Stopwatch.StartNew();
        var (analytics, requests, operations) = Charts(range, starts, values);
        Console.WriteLine($"{range}/{count}: three chart models built in {clock.Elapsed.TotalMilliseconds:F1}ms");
        var charts = new[] {
            analytics.Columns.Select(c => (c.X, c.Left, c.Width, c.Label)).ToArray(),
            requests.Columns.Select(c => (c.X, c.Left, c.Width, c.Label)).ToArray(),
            operations.Columns.Select(c => (c.X, c.Left, c.Width, Label: c.Header + ": Operations " + c.Value)).ToArray()
        };
        foreach (var columns in charts)
        {
            await Assert.That(columns.Length).IsEqualTo(count);
            var edge = 48d;
            for (var i = 0; i < count; i++)
            {
                await Assert.That(columns[i].Label).Contains(starts[i].ToString("MMM d", CultureInfo.InvariantCulture));
                await Assert.That(columns[i].Label).Contains(starts[i].ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC");
                await Assert.That(columns[i].Label).Contains(values[i].ToString("N0", CultureInfo.InvariantCulture));
                await Assert.That(Math.Abs(Number(columns[i].Left) - edge) < 0.001).IsTrue();
                await Assert.That(Number(columns[i].Width) > 0).IsTrue();
                edge = Number(columns[i].Left) + Number(columns[i].Width);
            }
            await Assert.That(Math.Abs(edge - 708) < 0.001).IsTrue();
            await Assert.That(columns[0].X).IsEqualTo("48.0");
            await Assert.That(columns[^1].X).IsEqualTo(count == 1 ? "48.0" : "708.0");
        }
        var missing = new bool[count]; missing[^1] = true;
        var gaps = AnalyticsLinePlot.For("Missing", range, starts, [new("Read", values, missing)]);
        await Assert.That(gaps.Columns[^1].Label).Contains("Read Not reported");
        await Assert.That(gaps.Columns[^1].Dots.Count).IsEqualTo(0);
    }

    [Test, Category("Slow")]
    [Arguments(168, 1920)]
    [Arguments(720, 1920)]
    [Arguments(168, 375)]
    [Arguments(720, 375)]
    public async Task KeyboardEndpointsAndEarlierTooltipHoverUseTheRenderedCharts(int count, int width)
    {
        var root = DashboardE2EFixture.FindRoot();
        var assets = Path.Combine(root, "src/TerraScale.Dashboard/web/assets");
        var starts = Enumerable.Range(0, count).Select(i => DateTimeOffset.UnixEpoch.AddHours(i)).ToArray();
        var values = Enumerable.Range(0, count).Select(i => i == count - 1 ? 999_999L : i + 10L).ToArray();
        var (analytics, requests, operations) = Charts("7d", starts, values);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/datastar.js", () => Results.File(Path.Combine(assets, "js/vendor/datastar.js"), "text/javascript"));
        app.MapGet("/", async (HttpContext http) =>
        {
            var context = new AccountContext(null!, new(), [], null, new() { Id = "01JA0000000000000000000001" });
            using var a = AnalyticsChartView.Create(new("Reads", analytics, new(context, "7d", "", ""))); a.HttpContext = http;
            using var r = HomeRequestChartView.Create(requests); r.HttpContext = http;
            using var o = ActivityChart.Create(operations);
            var css = await File.ReadAllTextAsync(Path.Combine(assets, "css/tokens.css")) + await File.ReadAllTextAsync(Path.Combine(assets, "css/data.css")) + await File.ReadAllTextAsync(Path.Combine(root, "src/TerraScale.Dashboard/Features/Analytics/analytics.css"));
            return Results.Content("<style>" + css + "</style><button id='before'>Before</button>" + await a.RenderAsync() + "<button data-chart-exit>After analytics</button>" + await r.RenderAsync() + "<button data-chart-exit>After requests</button>" + await o.RenderAsync() + "<button id='after'>After</button><script type='module' src='/datastar.js'></script>", "text/html");
        });
        await app.StartAsync();
        var origin = app.Urls.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 1080 } });
        page.SetDefaultTimeout(10000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        var clock = Stopwatch.StartNew();
        await page.GotoAsync(origin);
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.ts-chart-column[tabindex=\"-1\"]').length > 0");
        Console.WriteLine($"{count} buckets at {width}px × three charts: navigation and Datastar initialization {clock.Elapsed.TotalMilliseconds:F1}ms");
        // Enter through each production slice's final range link, including its real region
        // wrapper. Counting bucket tabindex values alone misses an extra wrapper/SVG stop.
        var sections = await page.Locator("section.ts-card:has(.ts-chart-shell)").AllAsync();
        await Assert.That(sections.Count).IsEqualTo(2);
        foreach (var section in sections)
        {
            var bucket = section.Locator(".ts-chart-column").Last;
            await section.Locator("nav a").Last.FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Assert.That(await bucket.EvaluateAsync<string>("el => el === document.activeElement ? 'bucket' : document.activeElement.outerHTML.slice(0, 200)")).IsEqualTo("bucket");
            await Assert.That(await section.Locator(".ts-chart-overlay").EvaluateAsync<bool>("el => el.classList.contains('is-active')")).IsTrue();
            await page.Keyboard.PressAsync("Tab");
            await Assert.That(await section.EvaluateAsync<bool>("el => document.activeElement === el.nextElementSibling && document.activeElement.hasAttribute('data-chart-exit')")).IsTrue();
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assert.That(await bucket.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
        }
        foreach (var chart in await page.Locator("svg.ts-chart[role=group]").AllAsync())
        {
            var columns = chart.Locator(".ts-chart-column"); var layer = chart.Locator(":scope > .ts-chart-overlay");
            await Assert.That(await columns.CountAsync()).IsEqualTo(count);
            await Assert.That(await chart.Locator(".ts-chart-column[tabindex='0']").CountAsync()).IsEqualTo(1);
            await columns.Last.FocusAsync(); await page.Keyboard.PressAsync("ArrowLeft");
            await Assert.That(await columns.Nth(count - 2).EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            await page.Keyboard.PressAsync("Home"); await page.Keyboard.PressAsync("ArrowLeft");
            await Assert.That((await columns.First.GetAttributeAsync("aria-label"))!.EndsWith(" 10", StringComparison.Ordinal)).IsTrue();
            await Assert.That(await columns.First.EvaluateAsync<bool>("el => el === document.activeElement && el.matches(':focus-visible')")).IsTrue();
            await Assert.That(await columns.First.Locator(".ts-chart-hit").EvaluateAsync<string>("el => getComputedStyle(el).stroke")).IsEqualTo("rgb(107, 147, 255)");
            await page.Keyboard.PressAsync("End"); await page.Keyboard.PressAsync("ArrowRight");
            await Assert.That(await columns.Last.GetAttributeAsync("aria-label")).Contains("999,999");
            await Assert.That(await columns.Last.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            await VisibleAsync();
            await Assert.That(await layer.Locator(".ts-chart-tip-value").TextContentAsync()).IsEqualTo("999,999");
            await page.Keyboard.PressAsync("Escape"); await HiddenAsync();
            await Assert.That(await columns.Last.EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            await page.Keyboard.PressAsync("ArrowLeft"); await VisibleAsync();
            await Assert.That(await layer.GetAttributeAsync("data-chart-bucket")).IsEqualTo((count - 2).ToString(CultureInfo.InvariantCulture));
            await Assert.That(await chart.Locator(".ts-chart-column[tabindex='0']").CountAsync()).IsEqualTo(1);
            await page.Keyboard.PressAsync("Tab");
            await Assert.That(await chart.EvaluateAsync<bool>("el => el.contains(document.activeElement)")).IsFalse();
            await page.Locator("#before").FocusAsync(); await page.Locator("#before").HoverAsync();
            await chart.ScrollIntoViewIfNeededAsync();
            // Dense mobile bands share pixels: take the actual earlier bucket selected by the
            // pointer, then require its timestamp/value to survive every step toward the box.
            var hit = (await columns.Nth(count / 8).Locator(".ts-chart-hit").BoundingBoxAsync())!;
            var startX = hit.X + hit.Width / 2; var startY = hit.Y + hit.Height * 0.75f;
            await page.Mouse.MoveAsync(startX, startY); await VisibleAsync();
            var key = (await layer.GetAttributeAsync("data-chart-bucket"))!;
            var index = int.Parse(key, CultureInfo.InvariantCulture);
            await Assert.That(index < count / 2).IsTrue();
            await Assert.That(Math.Abs(index - count / 8) <= Math.Ceiling(count / (await chart.BoundingBoxAsync())!.Width) + 1).IsTrue();
            var header = await columns.Nth(index).Locator(".ts-chart-tip-label").TextContentAsync();
            var box = (await layer.Locator(".ts-chart-tip-box").BoundingBoxAsync())!;
            var targetX = box.X + box.Width / 2; var targetY = box.Y + box.Height / 2;
            for (var step = 1; step <= 12; step++)
            {
                await page.Mouse.MoveAsync(startX + (targetX - startX) * step / 12, startY + (targetY - startY) * step / 12);
                await SameBucketAsync();
            }
            // This real locator hover failed before the overlay: later bands intercepted it.
            await layer.Locator(".ts-chart-tip-box").HoverAsync(new() { Position = new() { X = 2, Y = box.Height / 2 } }); await SameBucketAsync();
            await page.Mouse.MoveAsync(startX, targetY);
            var gapX = (startX + (startX < box.X ? box.X : box.X + box.Width)) / 2;
            await page.Mouse.MoveAsync(gapX, targetY); await SameBucketAsync();
            await Assert.That(await layer.EvaluateAsync<bool>("(el, p) => el.contains(document.elementFromPoint(p[0], p[1]))", new[] { (double)gapX, (double)targetY })).IsTrue();
            await page.Keyboard.PressAsync("Escape"); await HiddenAsync();
            await Assert.That(await page.Locator("#before").EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            await page.Locator("#before").HoverAsync(); await HiddenAsync();
            // The original columns retain chronology after overlay changes. On pointer exit,
            // preserve the keyboard-visible bucket; after focus exit, hide the overlay.
            await columns.First.FocusAsync(); await page.Keyboard.PressAsync("ArrowRight");
            await Assert.That(await columns.Nth(1).EvaluateAsync<bool>("el => el === document.activeElement")).IsTrue();
            await chart.ScrollIntoViewIfNeededAsync();
            var other = (await columns.Nth(count * 3 / 4).Locator(".ts-chart-hit").BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(other.X + other.Width / 2, other.Y + other.Height * 0.9f);
            await Assert.That(await layer.GetAttributeAsync("data-chart-bucket") != "1").IsTrue();
            await page.Locator("#before").HoverAsync(); await VisibleAsync();
            await Assert.That(await layer.GetAttributeAsync("data-chart-bucket")).IsEqualTo("1");
            await page.Locator("#before").FocusAsync(); await HiddenAsync();

            async Task VisibleAsync() => await Assert.That(await layer.EvaluateAsync<bool>("el => el.classList.contains('is-active') && getComputedStyle(el.querySelector('.ts-chart-tip')).visibility === 'visible'")).IsTrue();
            async Task HiddenAsync() => await Assert.That(await layer.EvaluateAsync<bool>("el => !el.classList.contains('is-active')")).IsTrue();
            async Task SameBucketAsync()
            {
                await VisibleAsync();
                await Assert.That(await layer.GetAttributeAsync("data-chart-bucket")).IsEqualTo(key);
                await Assert.That(await layer.Locator(".ts-chart-tip-label").TextContentAsync()).IsEqualTo(header);
                await Assert.That(await layer.Locator(".ts-chart-tip-value").TextContentAsync()).IsEqualTo(values[index].ToString("N0", CultureInfo.InvariantCulture));
            }
        }
        await Assert.That(errors).IsEmpty();
    }

    private static (AnalyticsLinePlot, HomeRequestChart, OperationChart) Charts(string range, DateTimeOffset[] starts, long[] values)
    {
        var measurement = new HomeMeasurement {
            Activity = starts.Select((s, i) => new ActivityPoint(s, values[i], 0)).ToArray(),
            RequestPoints = starts.Select((s, i) => new HomeRequestPoint(s, new Dictionary<string, long> { ["Read"] = values[i] })).ToArray()
        };
        var page = new HomePage(null!, "overview", range, measurement);
        return (AnalyticsLinePlot.For("Reads", range, starts, [new("Read", values)]), HomeRequestChart.For(page), OperationChart.For(page));
    }
    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}
