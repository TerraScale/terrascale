using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Authentication.Features.SigningKeys;
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
    private TerraBaseTestTenant? owner;
    // Separate databases avoid queueing every parallel management journey on one database writer.
    private readonly Lazy<Task<OidcHarness>>[] management;
    private int nextManagement = -1;
    private readonly Lazy<Task> initialized;

    public TerraBaseFixture()
    {
        this.management = Enumerable.Range(0, 4).Select(_ => new Lazy<Task<OidcHarness>>(this.StartManagementAsync)).ToArray();
        this.initialized = new(this.InitializeCoreAsync);
    }

    public TerraBaseTestServer Server => this.server ?? throw new InvalidOperationException("TerraBase fixture has not started.");

    internal Task<OidcHarness> ManagementAsync() =>
        this.management[(uint)Interlocked.Increment(ref this.nextManagement) % (uint)this.management.Length].Value;

    internal Task InitializeManagementAsync() => Task.WhenAll(this.management.Select(host => host.Value));

    private Task<OidcHarness> StartManagementAsync()
    {
        // The shared host belongs to the fixture, independently of the first borrowing test.
        using var flow = ExecutionContext.SuppressFlow();
        return Task.Run(() => OidcHarness.StartAsync(this, services: services => services.RemoveAll<KeyRingRefresh>()));
    }

    public Task InitializeAsync() => this.initialized.Value;

    private async Task InitializeCoreAsync()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        this.server = running ?? throw new InvalidOperationException($"Real TerraBase integration tests need TerraBase: {unavailable}");
        try
        {
            this.owner = await this.Server.CreateTenantAsync();
        }
        catch
        {
            await this.Server.DisposeAsync();
            this.server = null;
            throw;
        }
    }

    public async Task<(TerraBaseTestTenant Tenant, TerraBaseSqlDatabase Database)> CreateDatabaseAsync(bool provision = true)
    {
        // The fixture owns the Enterprise organization and its setup credential.
        // Each test still owns a separate project and database, without repeating
        // organization creation, plan changes and credential issuance in every journey.
        var shared = this.owner ?? throw new InvalidOperationException("TerraBase fixture has not initialized its owner.");
        var project = await this.Server.Management().CreateProjectAsync(shared.UserId, shared.Organization.Id, AuthStore.NewId());
        var tenant = shared with { Project = project };
        await this.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth").CreateAsync();
        var database = this.Server.Sql(tenant.ApiKey).Database(tenant.Project.Id, "auth");
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
        services.AddSingleton(provider => new TerraBaseSqlClient(provider.GetRequiredService<HttpClient>(), apiKey is null
            ? new TerraBaseSqlClientOptions { BaseAddress = this.Server.InternalAddress }
            : new TerraBaseSqlClientOptions { BaseAddress = this.Server.ClientAddress, AccessTokenProvider = _ => ValueTask.FromResult(apiKey) }).Database(tenant.Project.Id, databaseName));
        // Project closures reach this same regional TerraBase: its internal listener, with the peer credential.
        services.AddKeyedSingleton(App.StorageAuthority, (_, _) => this.Server.Management());
    }

    /// <summary>Uses only this region's storage transport; an unavailable local backend fails closed.</summary>
    public static void UseRegionalStorage(IServiceCollection services, TerraBaseTestTenant tenant, Uri ownRegion,
        string localDatabase = "auth", TerraBaseTransport.HostResolver? resolve = null)
    {
        services.AddSingleton(new HttpClient(TerraBaseTransport.CreateHandler([], NullLogger.Instance, resolve,
            peerSecret: TerraBaseTestServer.PeerSecret)));
        services.AddSingleton(provider => new TerraBaseSqlClient(provider.GetRequiredService<HttpClient>(),
            new TerraBaseSqlClientOptions { BaseAddress = ownRegion }).Database(tenant.Project.Id, localDatabase));
        services.AddKeyedSingleton(App.StorageAuthority, (provider, _) => new TerraBaseManagementClient(provider.GetRequiredService<HttpClient>(),
            new TerraBaseManagementClientOptions { BaseAddress = ownRegion }));
    }

    /// <summary>Creates and provisions another copy of the tenant's storage, <paramref name="name"/>: another region's.</summary>
    public async Task<TerraBaseSqlDatabase> CreateCopyAsync(TerraBaseTestTenant tenant, string name)
    {
        await this.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, name).CreateAsync();
        var copy = this.Server.Sql(tenant.ApiKey).Database(tenant.Project.Id, name);
        await new AuthStore(copy, TimeProvider.System).ProvisionAsync();
        return copy;
    }

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
        try
        {
            await Task.WhenAll(this.management.Where(host => host.IsValueCreated).Select(async host =>
                await (await host.Value).DisposeAsync()));
        }
        finally
        {
            if (this.server is not null)
            {
                await this.server.DisposeAsync();
            }
        }
    }
}

public sealed class StoreTestClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => this.UtcNow;
}
