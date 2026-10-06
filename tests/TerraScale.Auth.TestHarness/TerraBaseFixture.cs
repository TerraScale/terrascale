using System.Text.Json;
using TerraScale.Auth.Store;
using TerraScale.DotNet.Testing;
using TerraScale.DotNet.TerraBase;
using TUnit.Core.Interfaces;

namespace TerraScale.Auth.Tests;

/// <summary>One pinned real TerraBase process per suite; every test gets its own tenant and database.</summary>
public sealed class TerraBaseFixture : IAsyncInitializer, IAsyncDisposable
{
    private TerraBaseTestServer? server;

    public TerraBaseTestServer Server => this.server ?? throw new InvalidOperationException("TerraBase fixture has not started.");

    public async Task InitializeAsync()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        this.server = running ?? throw new InvalidOperationException($"Real TerraBase integration tests require the pinned binary: {unavailable}");
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
