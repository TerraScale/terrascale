using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
    /// Points a ts-auth server's storage at <paramref name="databaseName"/> of the tenant's project in this fixture.
    /// TODO(.cos/fly-migration-plan.md, phase 3a): TerraBase has no internal listener yet, so tests reach its TLS client
    /// listener with the tenant's API key; switch to the credential-free internal listener once it ships.
    /// The fixture's HttpClient trusts its certificate; a test may register its own, wrapping <see cref="NewHandler"/>.
    /// </summary>
    public void UseStorage(IServiceCollection services, TerraBaseTestTenant tenant, string databaseName = "auth", string? apiKey = null)
    {
        services.AddSingleton(this.Server.Http);
        services.AddSingleton(provider => new TerraBaseDocumentsClient(provider.GetRequiredService<HttpClient>(), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = new Uri(this.Server.ClientAddress.GetLeftPart(UriPartial.Authority)),
            ApiKey = apiKey ?? tenant.ApiKey,
        }).Database(tenant.Project.Id, databaseName));
    }

    public HttpClient NewHttpClient() => new(this.NewHandler());

    /// <summary>A handler that trusts only this fixture's TerraBase certificate.</summary>
    public SocketsHttpHandler NewHandler()
    {
        var trusted = this.Server.Certificate.RawData;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(trusted) },
        };
    }

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
