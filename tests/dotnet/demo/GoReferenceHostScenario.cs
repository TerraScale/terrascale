using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E;

// A plain Go executable serves the original application. TUnit owns its
// lifecycle and browser assertions; no second test framework runs underneath.
public sealed class GoReferenceHostScenario : IAsyncDisposable
{
    private Process? host;
    private Task<string>? errors;
    private string? directory;
    public Uri Origin { get; private set; } = null!;
    public Uri NativeOrigin { get; private set; } = null!;

    public static async Task<GoReferenceHostScenario> StartAsync(string root)
    {
        var scenario = new GoReferenceHostScenario();
        try { await scenario.InitializeAsync(root); return scenario; }
        catch { await scenario.DisposeAsync(); throw; }
    }

    private async Task InitializeAsync(string root)
    {
        directory = Path.Combine(Path.GetTempPath(), "terrascale-go-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "go-reference");
        var build = new ProcessStartInfo("go")
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "build", "-mod=vendor", "-trimpath", "-buildvcs=false", "-o", executable, "./tests/dotnet/go-reference" })
            build.ArgumentList.Add(argument);
        using (var process = Process.Start(build) ?? throw new IOException("Could not build the original Go reference host."))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            if (process.ExitCode != 0) throw new IOException("Original Go reference host build failed: " + await output + await error);
        }
        host = Process.Start(new ProcessStartInfo(executable)
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new IOException("Could not start the original Go reference host.");
        errors = host.StandardError.ReadToEndAsync();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var ready = await host.StandardOutput.ReadLineAsync(deadline.Token);
            if (ready is null) throw new IOException("Original Go reference host exited before readiness: " + await errors);
            using var addresses = JsonDocument.Parse(ready);
            Origin = new(addresses.RootElement.GetProperty("origin").GetString()!);
            NativeOrigin = new(addresses.RootElement.GetProperty("nativeOrigin").GetString()!);
            if (Origin.Host != "127.0.0.1" || NativeOrigin.Host != "127.0.0.1")
                throw new IOException("The test-only Go reference must bind exclusively to loopback.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            foreach (var origin in new[] { Origin, NativeOrigin })
            {
                using var response = await client.GetAsync(new Uri(origin, "/healthz"), deadline.Token);
                response.EnsureSuccessStatusCode();
            }
        }
    }

    public HttpClient CreateClient() => new(new HttpClientHandler
    { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
    { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(30) };

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            if (!host.HasExited)
            {
                var stop = new ProcessStartInfo("kill");
                stop.ArgumentList.Add("-TERM");
                stop.ArgumentList.Add(host.Id.ToString());
                using var signal = Process.Start(stop) ?? throw new IOException("Could not stop the original Go reference host.");
                await signal.WaitForExitAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await host.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync(); }
            }
            if (errors is not null)
            {
                var output = await errors;
                if (!string.IsNullOrWhiteSpace(output)) Console.WriteLine(output);
            }
            host.Dispose();
            host = null;
        }
        if (directory is not null && Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); directory = null; }
    }
}
