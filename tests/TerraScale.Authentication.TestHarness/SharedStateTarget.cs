using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.RateLimiting;
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
        // ProvisionAsync already creates the collections readiness requires.
        foreach (var collection in new[] { AttemptLimiter.Collection }.Except(AuthStore.RequiredCollections))
        {
            await database.CreateCollectionAsync(collection);
        }

        return new SharedStateTarget(fixture, tenant, database);
    }

    /// <summary>Points the server's storage at this database, through <paramref name="apiKey"/> instead of the tenant's own key when given.</summary>
    public void Register(IServiceCollection services, string? apiKey = null) => this.fixture.UseStorage(services, this.tenant, apiKey: apiKey);

    /// <summary>A stored document, read directly from TerraBase, or <see langword="null"/>.</summary>
    public async Task<JsonElement?> ReadAsync(string collection, string instanceId, string key) =>
        (await this.Database.GetDocumentAsync(collection, AuthStore.NaturalKeyId(instanceId, collection, key)))?.Document;

    /// <summary>How many documents a collection holds.</summary>
    public async Task<int> CountAsync(string collection) => (await this.Database.ListDocumentsAsync(collection)).Items.Count;
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
