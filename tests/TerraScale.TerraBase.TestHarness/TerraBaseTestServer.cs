using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Configuration;

namespace TerraScale.TerraBase.Testing;

/// <summary>
/// Runs the real TerraBase application on an ephemeral loopback port and talks to it over
/// HTTP. Linked into both suites as source; deliberately not a third test project.
/// </summary>
public sealed class TerraBaseTestServer : IAsyncDisposable
{
    private TerraBaseTestServer(WebApplication app, Uri baseAddress)
    {
        App = app;
        BaseAddress = baseAddress;
        Client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>The running application; use only to resolve services registered by the test.</summary>
    public WebApplication App { get; }

    /// <summary>The loopback origin of the administrative listener.</summary>
    public Uri BaseAddress { get; }

    /// <summary>A client that never follows redirects and never stores cookies.</summary>
    public HttpClient Client { get; }

    /// <summary>
    /// Starts a server. <paramref name="settings"/> edits the defaults and
    /// <paramref name="services"/> replaces infrastructure.
    /// </summary>
    public static async Task<TerraBaseTestServer> StartAsync(
        Action<Dictionary<string, string>>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["admin-port"] = "0" };
        settings?.Invoke(values);
        var options = TerraBaseOptions.FromValues(values);

        var app = TerraBase.App.Build(options, builder => services?.Invoke(builder.Services));
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("the server did not report its listening addresses");
        var address = addresses.Addresses.Single();
        return new TerraBaseTestServer(app, new Uri(address));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
