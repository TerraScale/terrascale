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
    private TerraBaseTestServer(WebApplication app, Uri baseAddress, Uri? peerAddress)
    {
        App = app;
        BaseAddress = baseAddress;
        PeerAddress = peerAddress;
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

    /// <summary>
    /// The <c>https://localhost</c> origin of the inter-node gRPC listener, or <see langword="null"/>
    /// when the test did not enable it (see <see cref="PeerTls"/>).
    /// </summary>
    public Uri? PeerAddress { get; }

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
        var listeners = addresses.Addresses.Select(address => new Uri(address)).ToList();
        var admin = listeners.Single(address => address.Scheme == Uri.UriSchemeHttp);
        var peer = listeners.SingleOrDefault(address => address.Scheme == Uri.UriSchemeHttps);
        // The peer certificate is issued for localhost; the listener itself binds every interface.
        var peerAddress = peer is null ? null : new UriBuilder(peer) { Host = "localhost" }.Uri;
        return new TerraBaseTestServer(app, admin, peerAddress);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
