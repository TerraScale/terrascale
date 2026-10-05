using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Engine.Sqlite;

namespace TerraScale.TerraBase.Testing;

/// <summary>
/// Runs the real TerraBase <c>Program</c> on real Kestrel listeners bound to ephemeral ports,
/// with its own temporary data directory. Linked into both suites as source; deliberately not
/// a third test project.
/// </summary>
public sealed class TerraBaseTestServer : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IStorageEngine _engine;

    // The temporary directory this harness created, deleted on dispose even when a test chose another data directory.
    private readonly string _temporaryDirectory;

    private TerraBaseTestServer(
        WebApplicationFactory<Program> factory, string temporaryDirectory, IReadOnlyList<Uri> listeners, IReadOnlyDictionary<string, string> settings)
    {
        _factory = factory;
        _engine = factory.Services.GetRequiredService<IStorageEngine>();
        _temporaryDirectory = temporaryDirectory;
        DataDirectory = Path.GetFullPath(settings["data-directory"]);
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

    /// <summary>
    /// The server's data directory: its own temporary one, deleted on dispose, unless the test set <c>data-directory</c>
    /// (which the test then owns).
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>The loopback origin of the administrative listener.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The <c>https://localhost</c> origin of the client listener, when enabled (see <see cref="ClientTls"/>).</summary>
    public Uri? ClientAddress { get; }

    /// <summary>The <c>https://localhost</c> origin of the inter-node gRPC listener, when enabled (see <see cref="PeerTls"/>).</summary>
    public Uri? PeerAddress { get; }

    /// <summary>A client for the administrative listener that never follows redirects and never stores cookies.</summary>
    public HttpClient Client { get; }

    /// <summary>The running server's services, for checks no listener exposes (for example engine transactions or its endpoints).</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>
    /// Whether a process (this one included) owns <paramref name="dataDirectory"/>: tries the exclusive lock an engine
    /// holds on its <c>.terrabase.lock</c>, and gives it back at once.
    /// </summary>
    public static bool IsDataDirectoryOwned(string dataDirectory)
    {
        try
        {
            using var attempt = new FileStream(Path.Combine(dataDirectory, ".terrabase.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException error) when (SqliteStorageEngine.IsLockConflict(error))
        {
            // Only the lock being held, the same test the engine applies. Every other failure (a missing directory, a
            // full disk, a broken lock file) propagates, so a broken probe never reads as an owned directory.
            return true;
        }
    }

    /// <summary>
    /// Prepares an empty directory as a data directory of this build, as a server's first start does, so a test can
    /// seed it with files (such as the system database) before starting a server on it.
    /// </summary>
    public static async Task InitializeDataDirectoryAsync(string dataDirectory)
    {
        await using (await StartAsync(settings => settings["data-directory"] = dataDirectory))
        {
        }
    }

    /// <summary>
    /// Starts a server. <paramref name="settings"/> edits the <c>--name=value</c> settings;
    /// <paramref name="services"/> replaces services after the application registered its own
    /// (for example a <see cref="TimeProvider"/> that tests move forward).
    /// <paramref name="environmentName"/> selects the host environment without changing process-wide variables.
    /// </summary>
    public static async Task<TerraBaseTestServer> StartAsync(
        Action<Dictionary<string, string>>? settings = null, Action<IServiceCollection>? services = null, string environmentName = "Development")
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-test-").FullName;
        WebApplicationFactory<Program>? factory = null;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["admin-port"] = "0",
                ["data-directory"] = dataDirectory,
                ["region"] = "test-region",
            };
            settings?.Invoke(values);

            // UseSetting values reach Program as --name=value entry-point arguments. Every other
            // setting is passed empty, which clears it, so ambient TERRABASE_* variables of the
            // test process never reach a test server.
            var arguments = TerraBaseOptions.SettingNames.ToDictionary(name => name, _ => string.Empty, StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                arguments[key] = value;
            }

            factory = new Factory(arguments, services, environmentName);
            factory.UseKestrel();
            IServerAddressesFeature addresses;
            try
            {
                // Factory startup blocks waiting for the host; keep parallel starts off request workers.
                await Task.Factory.StartNew(factory.StartServer, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                    ?? throw new InvalidOperationException("the server did not report its listening addresses");
            }
            catch (ObjectDisposedException error) when (string.Equals(error.ObjectName, "IServiceProvider", StringComparison.Ordinal))
            {
                // Program disposes the host before returning on a failed startup, releasing its directory lock.
                throw new InvalidOperationException("the server exited before it listened; see its error output", error);
            }

            if (addresses.Addresses.Count == 0)
            {
                // Program returned (for example with exit code 2 and a message on stderr) instead of running.
                throw new InvalidOperationException("the server exited before it listened; see its error output");
            }

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
        // Program's RunAsync disposes the host on its own thread, which can still be closing database
        // files when the factory returns. Disposing the engine again is a no-op that returns only once
        // its files are closed (or closes them first).
        await _engine.DisposeAsync();
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private sealed class Factory(IReadOnlyDictionary<string, string> settings, Action<IServiceCollection>? services, string environmentName) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            if (services is not null)
            {
                builder.ConfigureTestServices(services);
            }
        }
    }
}
