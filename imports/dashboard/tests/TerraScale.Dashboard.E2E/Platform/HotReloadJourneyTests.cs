using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
// Needs this machine's tailnet name and dotnet watch, so it runs on a dev machine: make test-hot-reload.
[Explicit]
public sealed class HotReloadJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task CSharpAndCssChangesReachTheOpenTailscaleBrowserAndRestore()
    {
        var hash = Convert.ToUInt32(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Root)))[..8], 16);
        var origin = new Uri($"http://{await TailnetHostAsync()}:{7500 + hash % 1000}");
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
            await AssertHotReloadAsync(origin.ToString().TrimEnd('/'));
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

    private async Task AssertHotReloadAsync(string origin)
    {
        var source = Path.Combine(fixture.Root, "src/TerraScale.Dashboard/Features/Auth/AuthEndpoints.cs");
        var css = Path.Combine(fixture.Root, "src/TerraScale.Dashboard/Features/Auth/auth.css");
        var originalSource = await File.ReadAllTextAsync(source);
        var originalCss = await File.ReadAllTextAsync(css);
        const string originalCopy = "This sign-in link expired or was started in another browser. Sign in again.";
        const string changedCopy = originalCopy + " Hot reload check.";
        await Assert.That(originalSource).Contains(originalCopy);
        var changedSource = originalSource.Replace(originalCopy, changedCopy);
        var changedCss = originalCss + "\n.ts-login-brand { opacity: 0.99; }\n";
        var sourceChanged = false;
        var cssChanged = false;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        try
        {
            var page = await browser.NewPageAsync();
            page.SetDefaultTimeout(45000);
            var ready = page.Locator("#ts-dev-updates[data-dev-ready=\"true\"]");
            // The sign-in failure page is the one auth page the dashboard still renders itself.
            await page.GotoAsync(origin + "/login/callback?state=hot-reload");
            await ready.WaitForAsync(new() { State = WaitForSelectorState.Attached });
            await File.WriteAllTextAsync(source, changedSource);
            sourceChanged = true;
            await page.GetByText(changedCopy, new() { Exact = true }).WaitForAsync();
            await Assert.That(await File.ReadAllTextAsync(source)).IsEqualTo(changedSource).Because("No concurrent edit to the source fixture");
            await File.WriteAllTextAsync(source, originalSource);
            sourceChanged = false;
            await page.GetByText(originalCopy, new() { Exact = true }).WaitForAsync();
            await ready.WaitForAsync(new() { State = WaitForSelectorState.Attached });
            var opacity = await page.Locator(".ts-login-brand").EvaluateAsync<string>("element => getComputedStyle(element).opacity");
            await File.WriteAllTextAsync(css, changedCss);
            cssChanged = true;
            await page.WaitForFunctionAsync("() => getComputedStyle(document.querySelector('.ts-login-brand')).opacity === '0.99'");
            await Assert.That(await File.ReadAllTextAsync(css)).IsEqualTo(changedCss).Because("No concurrent edit to the CSS fixture");
            await File.WriteAllTextAsync(css, originalCss);
            cssChanged = false;
            await page.WaitForFunctionAsync("value => getComputedStyle(document.querySelector('.ts-login-brand')).opacity === value", opacity);
            Console.WriteLine("PASS: C# hot reload and CSS updates reached the open browser over the Tailscale origin; source fixtures restored");
        }
        finally
        {
            if (sourceChanged && await File.ReadAllTextAsync(source) == changedSource) await File.WriteAllTextAsync(source, originalSource);
            if (cssChanged && await File.ReadAllTextAsync(css) == changedCss) await File.WriteAllTextAsync(css, originalCss);
        }
    }

    // scripts/demo.sh serves on this machine's tailnet name too.
    private static async Task<string> TailnetHostAsync()
    {
        var start = new ProcessStartInfo("tailscale") { RedirectStandardOutput = true };
        start.ArgumentList.Add("status");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start) ?? throw new IOException("Could not read this machine's Tailscale name.");
        using var status = JsonDocument.Parse(await process.StandardOutput.ReadToEndAsync());
        await process.WaitForExitAsync();
        var name = status.RootElement.GetProperty("Self").GetProperty("DNSName").GetString()?.TrimEnd('.');
        return string.IsNullOrEmpty(name) ? throw new IOException("Tailscale is not logged in on this machine.") : name;
    }
}
