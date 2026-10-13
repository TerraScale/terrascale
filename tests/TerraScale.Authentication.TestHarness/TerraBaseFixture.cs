using System.Net;
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
    /// default through TerraBase's internal listener without a credential, as ts-auth reaches it on Fly; with
    /// <paramref name="apiKey"/>, through the public listener with that key. A test may register its own HttpClient,
    /// wrapping <see cref="NewHandler"/>.
    /// </summary>
    public void UseStorage(IServiceCollection services, TerraBaseTestTenant tenant, string databaseName = "auth", string? apiKey = null)
    {
        services.AddSingleton(this.Server.Http);
        services.AddSingleton(provider => new TerraBaseDocumentsClient(provider.GetRequiredService<HttpClient>(), apiKey is null
            ? new TerraBaseDocumentsClientOptions { BaseAddress = this.Server.InternalAddress }
            : new TerraBaseDocumentsClientOptions { BaseAddress = this.Server.ClientAddress, ApiKey = apiKey }).Database(tenant.Project.Id, databaseName));
    }

    /// <summary>
    /// Points storage at the tenant's databases as ts-auth reaches them on Fly (internal listeners, no credential), through
    /// ts-auth's own transports: reads of GET and HEAD requests at <paramref name="ownRegion"/> (database
    /// <paramref name="localDatabase"/>), at <paramref name="otherRegion"/> when it does not answer; everything else at
    /// <paramref name="designatedRegion"/> (database <paramref name="designatedDatabase"/>), with no fallback. Two database
    /// names stand for two regions' copies that have not replicated.
    /// </summary>
    public static void UseRegionalStorage(IServiceCollection services, TerraBaseTestTenant tenant, Uri ownRegion, TcpRelay otherRegion, Uri designatedRegion,
        string localDatabase = "auth", string designatedDatabase = "auth")
    {
        services.AddSingleton(new HttpClient(TerraBaseTransport.CreateHandler([new DnsEndPoint("127.0.0.1", otherRegion.Port)], NullLogger.Instance)));
        services.AddSingleton(provider => new TerraBaseDocumentsClient(provider.GetRequiredService<HttpClient>(),
            new TerraBaseDocumentsClientOptions { BaseAddress = ownRegion }).Database(tenant.Project.Id, localDatabase));
        services.AddKeyedSingleton(App.StorageAuthority, new HttpClient(TerraBaseTransport.CreateHandler([], NullLogger.Instance)));
        services.AddKeyedSingleton(App.StorageAuthority, (provider, _) => new TerraBaseDocumentsClient(provider.GetRequiredKeyedService<HttpClient>(App.StorageAuthority),
            new TerraBaseDocumentsClientOptions { BaseAddress = designatedRegion }).Database(tenant.Project.Id, designatedDatabase));
    }

    /// <summary>Creates and provisions another copy of the tenant's storage, <paramref name="name"/>: another region's.</summary>
    public async Task<TerraBaseDatabase> CreateCopyAsync(TerraBaseTestTenant tenant, string name)
    {
        var copy = this.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, name);
        await copy.CreateAsync();
        await new AuthStore(copy, TimeProvider.System).ProvisionAsync();
        return copy;
    }

    public static HttpClient NewHttpClient() => new(NewHandler());

    /// <summary>A handler for this fixture's TerraBase that never follows redirects.</summary>
    public static SocketsHttpHandler NewHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

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
