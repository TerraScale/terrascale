using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.TerraBase.Testing;

/// <summary>
/// Runs the real TerraBase <c>Program</c> on real Kestrel listeners bound to ephemeral ports,
/// with its own temporary data directory. Linked into both suites as source; deliberately not
/// a third test project.
/// </summary>
public sealed class TerraBaseTestServer : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private TerraBaseTestServer(
        WebApplicationFactory<Program> factory, string dataDirectory, IReadOnlyList<Uri> listeners, IReadOnlyDictionary<string, string> settings)
    {
        _factory = factory;
        DataDirectory = dataDirectory;
        BaseAddress = listeners.Single(address => address.Scheme == Uri.UriSchemeHttp);
        Client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };
        // HTTPS listeners bind every interface; certificates in tests are issued for localhost.
        var https = listeners.Where(address => address.Scheme == Uri.UriSchemeHttps)
            .Select(address => new UriBuilder(address) { Host = "localhost" }.Uri)
            .ToList();
        // Kestrel reports listeners in bind order: admin, client, peer.
        ClientAddress = settings.ContainsKey("client-port") ? https[0] : null;
        PeerAddress = settings.ContainsKey("peer-port") ? https[^1] : null;
    }

    /// <summary>This server's own temporary data directory, deleted on dispose.</summary>
    public string DataDirectory { get; }

    /// <summary>The loopback origin of the administrative listener.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The <c>https://localhost</c> origin of the client listener, when enabled (see <see cref="ClientTls"/>).</summary>
    public Uri? ClientAddress { get; }

    /// <summary>The <c>https://localhost</c> origin of the inter-node gRPC listener, when enabled (see <see cref="PeerTls"/>).</summary>
    public Uri? PeerAddress { get; }

    /// <summary>A client for the administrative listener that never follows redirects and never stores cookies.</summary>
    public HttpClient Client { get; }

    /// <summary>Starts a server. <paramref name="settings"/> edits the <c>--name=value</c> settings.</summary>
    public static async Task<TerraBaseTestServer> StartAsync(Action<Dictionary<string, string>>? settings = null)
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-test-").FullName;
        WebApplicationFactory<Program>? factory = null;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["admin-port"] = "0",
                ["data-directory"] = dataDirectory,
            };
            settings?.Invoke(values);

            // UseSetting values reach Program as --name=value entry-point arguments.
            factory = new Factory(values);
            factory.UseKestrel();
            factory.StartServer();
            var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("the server did not report its listening addresses");
            return new TerraBaseTestServer(factory, dataDirectory, [.. addresses.Addresses.Select(address => new Uri(address))], values);
        }
        catch
        {
            // The caller gets nothing to dispose: stop the host and remove its directory here.
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            Directory.Delete(dataDirectory, recursive: true);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        Directory.Delete(DataDirectory, recursive: true);
    }

    private sealed class Factory(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
