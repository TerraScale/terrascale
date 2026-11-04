using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// A fresh tenant database on the real TerraBase fixture, provisioned with the infrastructure
/// collections and the attempt-limit collection. Several servers bound to one
/// target are replicas of one deployment.
/// </summary>
public sealed class SharedStateTarget
{
    private readonly TerraBaseFixture fixture;
    private readonly TerraBaseTestTenant tenant;

    private SharedStateTarget(TerraBaseFixture fixture, TerraBaseTestTenant tenant, TerraBaseDatabase database)
    {
        this.fixture = fixture;
        this.tenant = tenant;
        this.Database = database;
    }

    /// <summary>The database every bound server stores in.</summary>
    public TerraBaseDatabase Database { get; }

    /// <summary>Creates and provisions a database for one test.</summary>
    public static async Task<SharedStateTarget> CreateAsync(TerraBaseFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        return new SharedStateTarget(fixture, tenant, database);
    }

    public void Register(IServiceCollection services, string? apiKey = null) => this.fixture.UseStorage(services, this.tenant, apiKey: apiKey);

    public async Task<JsonElement?> ReadAsync(string collection, string instanceId, string key)
    {
        var rows = await this.Database.QuerySqlAsync($"SELECT body FROM \"{collection}\" WHERE id = ?", [AuthStore.NaturalKeyId(instanceId, collection, key)]);
        if (rows.Count == 0)
        {
            return null;
        }

        using var body = JsonDocument.Parse(rows[0].GetString(0));
        return body.RootElement.Clone();
    }

    public async Task<int> CountAsync(string collection) =>
        (int)(await this.Database.QuerySqlAsync($"SELECT count(*) FROM \"{collection}\""))[0].GetInt64(0);
}

/// <summary>A wall clock the test moves by hand.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long ticks = start.UtcTicks;

    /// <summary>Moves the clock forward (or back, for a negative span).</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref this.ticks, by.Ticks);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref this.ticks), TimeSpan.Zero);
}
