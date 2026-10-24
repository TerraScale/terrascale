using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using TUnit.Core.Interfaces;

namespace TerraScale.Authentication.Tests;

/// <summary>One real TerraBase process per suite, built from this commit; every test gets its own tenant and database.</summary>
public sealed class TerraBaseFixture : IAsyncInitializer, IAsyncDisposable
{
    private TerraBaseTestServer? server;

    public TerraBaseTestServer Server => this.server ?? throw new InvalidOperationException("TerraBase fixture has not started.");

    public async Task InitializeAsync()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        this.server = running ?? throw new InvalidOperationException($"Real TerraBase integration tests need TerraBase: {unavailable}");
    }

    public async Task<(TerraBaseTestTenant Tenant, TerraBaseDatabase Database)> CreateDatabaseAsync(bool provision = true)
    {
        var tenant = await this.Server.CreateTenantAsync();
        var database = this.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth");
        await database.CreateAsync();
        if (provision)
        {
            await new AuthStore(database, TimeProvider.System).ProvisionAsync();
        }

        return (tenant, database);
    }

    /// <summary>
    /// Points a ts-auth server's storage at <paramref name="databaseName"/> of the tenant's project in this fixture: by
    /// default through TerraBase's internal listener with the peer credential, as ts-auth reaches it over NetBird; with
    /// <paramref name="apiKey"/>, through the public listener with that key. A test may register its own HttpClient,
    /// wrapping <see cref="NewHandler"/>.
    /// </summary>
    public void UseStorage(IServiceCollection services, TerraBaseTestTenant tenant, string databaseName = "auth", string? apiKey = null)
    {
        services.AddSingleton(apiKey is null ? this.Server.Http : this.Server.PublicHttp);
        services.AddSingleton(provider => new TerraBaseDocumentsClient(provider.GetRequiredService<HttpClient>(), apiKey is null
            ? new TerraBaseDocumentsClientOptions { BaseAddress = this.Server.InternalAddress }
            : new TerraBaseDocumentsClientOptions { BaseAddress = this.Server.ClientAddress, ApiKey = apiKey }).Database(tenant.Project.Id, databaseName));
        // Project closures reach this same regional TerraBase: its internal listener, with the peer credential.
        services.AddKeyedSingleton(App.StorageAuthority, (_, _) => this.Server.Management());
    }

    /// <summary>Uses only this region's storage transport; an unavailable local backend fails closed.</summary>
    public static void UseRegionalStorage(IServiceCollection services, TerraBaseTestTenant tenant, Uri ownRegion,
        string localDatabase = "auth", TerraBaseTransport.HostResolver? resolve = null)
    {
        services.AddSingleton(new HttpClient(TerraBaseTransport.CreateHandler([], NullLogger.Instance, resolve,
            peerSecret: TerraBaseTestServer.PeerSecret)));
        services.AddSingleton(provider => new TerraBaseDocumentsClient(provider.GetRequiredService<HttpClient>(),
            new TerraBaseDocumentsClientOptions { BaseAddress = ownRegion }).Database(tenant.Project.Id, localDatabase));
        services.AddKeyedSingleton(App.StorageAuthority, (provider, _) => new TerraBaseManagementClient(provider.GetRequiredService<HttpClient>(),
            new TerraBaseManagementClientOptions { BaseAddress = ownRegion }));
    }

    /// <summary>Creates and provisions another copy of the tenant's storage, <paramref name="name"/>: another region's.</summary>
    public async Task<TerraBaseDatabase> CreateCopyAsync(TerraBaseTestTenant tenant, string name)
    {
        var copy = this.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, name);
        await copy.CreateAsync();
        await new AuthStore(copy, TimeProvider.System).ProvisionAsync();
        return copy;
    }

    public static HttpClient NewHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    /// <summary>A handler for this fixture's TerraBase that never follows redirects and sends the peer credential.</summary>
    public static HttpMessageHandler NewHandler() => new PeerCredentialHandler(TerraBaseTestServer.PeerSecret)
    {
        InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false },
    };

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (this.server is not null)
        {
            await this.server.DisposeAsync();
        }
    }
}

public sealed class StoreTestClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => this.UtcNow;
}
