using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Interfaces;

namespace TerraScale.Dashboard.E2E;

public sealed class DashboardE2EFixture : IAsyncInitializer, IAsyncDisposable
{
    private WebApplication? first;
    private WebApplication? second;
    public string Root { get; } = FindRoot();
    public Uri Origin { get; private set; } = null!;
    public Uri ReplicaOrigin { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        first = environment.CreateHost(Root, 0, "http://127.0.0.1");
        try
        {
            await first.StartAsync();
            Origin = Address(first);
            second = environment.CreateHost(Root, 0, Origin.ToString().TrimEnd('/'));
            await second.StartAsync();
            ReplicaOrigin = Address(second);
            using var client = CreateClient();
            using var response = await client.GetAsync("/readyz");
            response.EnsureSuccessStatusCode();
        }
        catch { await DisposeAsync(); throw; }
    }

    public HttpClient CreateClient(bool replica = false) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    })
    { BaseAddress = replica ? ReplicaOrigin : Origin, Timeout = TimeSpan.FromSeconds(30) };

    public async ValueTask DisposeAsync()
    {
        if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); second = null; }
        if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); first = null; }
    }

    private static Uri Address(WebApplication application) => new(application.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.Dashboard.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("The E2E executable must run from the dashboard checkout.");
    }
}

public static class BrowserJourneys
{
    public static async Task RunAsync(DashboardE2EFixture fixture, string script)
        => await RunAsync(fixture.Root, script, fixture.Origin);

    public static async Task RunAsync(string root, string script, Uri origin)
    {
        var start = new ProcessStartInfo("node") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine("tests", "dotnet", script));
        start.ArgumentList.Add(origin.ToString().TrimEnd('/'));
        using var process = Process.Start(start) ?? throw new IOException("Could not start the E2E browser journey.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        var stdout = await output;
        var stderr = await errors;
        await Assert.That(process.ExitCode).IsEqualTo(0).Because(stdout + stderr);
        Console.WriteLine(stdout);
    }
}
