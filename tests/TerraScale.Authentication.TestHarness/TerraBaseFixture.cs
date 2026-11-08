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

/// <summary>A small pool of real TerraBase processes per suite, built from this commit; every test gets its own tenant and database on one pool member.</summary>
public sealed class TerraBaseFixture : IAsyncInitializer, IAsyncDisposable
{
    private static readonly int PoolSize = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
    private readonly List<TerraBaseTestServer> started = [];
    private (TerraBaseTestServer Server, TerraBaseTestTenant Owner)[]? pool;
    private int nextServer = -1;
    // Separate databases avoid queueing every parallel management journey on one database writer.
    private readonly Lazy<Task<OidcHarness>>[] management;
    private int nextManagement = -1;
    private readonly Lazy<Task> initialized;

    public TerraBaseFixture()
    {
        this.management = Enumerable.Range(0, 4).Select(_ => new Lazy<Task<OidcHarness>>(this.StartManagementAsync)).ToArray();
        this.initialized = new(this.InitializeCoreAsync);
    }

    public TerraBaseTestServer Server => this.pool?[0].Server ?? throw new InvalidOperationException("TerraBase fixture has not started.");

    public TerraBaseTestServer ServerFor(TerraBaseTestTenant tenant) => tenant.Server ?? this.Server;

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
        var members = new (TerraBaseTestServer Server, TerraBaseTestTenant Owner)[PoolSize];
        try
        {
            await Task.WhenAll(Enumerable.Range(0, PoolSize).Select(async index => members[index] = await this.StartPoolMemberAsync()));
        }
        catch
        {
            foreach (var server in this.started)
            {
                await server.DisposeAsync();
            }

            this.started.Clear();
            throw;
        }

        this.pool = members;
    }

    private async Task<(TerraBaseTestServer Server, TerraBaseTestTenant Owner)> StartPoolMemberAsync()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        var server = running ?? throw new InvalidOperationException($"Real TerraBase integration tests need TerraBase: {unavailable}");
        this.started.Add(server);
        try
        {
            return (server, await server.CreateTenantAsync());
        }
        catch
        {
            this.started.Remove(server);
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task<(TerraBaseTestTenant Tenant, TerraBaseSqlDatabase Database)> CreateDatabaseAsync(bool provision = true)
    {
        var members = this.pool ?? throw new InvalidOperationException("TerraBase fixture has not initialized its pool.");
        // Each member owns the Enterprise organization and its setup credential; tests are handed out round-robin
        // so parallel writes do not queue on one TerraBase process. Each test still owns a separate project and
        // database, without repeating organization creation, plan changes and credential issuance in every journey.
        var (server, owner) = members[(uint)Interlocked.Increment(ref this.nextServer) % (uint)members.Length];
        var project = await server.Management().CreateProjectAsync(owner.UserId, owner.Organization.Id, AuthStore.NewId());
        var tenant = owner with { Project = project, Server = server };
        await server.Databases(tenant.ApiKey).Database(tenant.Project.Id, "auth").CreateAsync();
        var database = server.Sql(tenant.ApiKey).Database(tenant.Project.Id, "auth");
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
        var server = this.ServerFor(tenant);
        services.AddSingleton(apiKey is null ? server.Http : server.PublicHttp);
        services.AddSingleton(provider => new TerraBaseSqlClient(provider.GetRequiredService<HttpClient>(), apiKey is null
            ? new TerraBaseSqlClientOptions { BaseAddress = server.InternalAddress }
            : new TerraBaseSqlClientOptions { BaseAddress = server.ClientAddress, AccessTokenProvider = _ => ValueTask.FromResult(apiKey) }).Database(tenant.Project.Id, databaseName));
        // Project closures reach this same regional TerraBase: its internal listener, with the peer credential.
        services.AddKeyedSingleton(App.StorageAuthority, (_, _) => server.Management());
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
        var server = this.ServerFor(tenant);
        await server.Databases(tenant.ApiKey).Database(tenant.Project.Id, name).CreateAsync();
        var copy = server.Sql(tenant.ApiKey).Database(tenant.Project.Id, name);
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
        using var rowData = JsonDocument.Parse(json);
        return rowData.RootElement.Clone();
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
            foreach (var server in this.started)
            {
                await server.DisposeAsync();
            }
        }
    }
}

public sealed class StoreTestClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => this.UtcNow;
}
