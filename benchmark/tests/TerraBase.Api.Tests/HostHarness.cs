using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace TerraBase.Api.Tests;

/// <summary>Runs the real host on an ephemeral loopback port.</summary>
public sealed class HostHarness : IAsyncDisposable
{
    private readonly WebApplication app;

    private HostHarness(WebApplication app)
    {
        this.app = app;
        Http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
    }

    public HttpClient Http { get; }

    public MetricsRegistry Metrics => app.Services.GetRequiredService<MetricsRegistry>();

    public static async Task<HostHarness> StartAsync()
    {
        var app = TerraBaseHost.Create("http://127.0.0.1:0");
        try
        {
            await app.StartAsync().ConfigureAwait(false);
            return new HostHarness(app);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
