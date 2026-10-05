using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Store;
using TerraScale.DotNet.Testing;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Tests;

/// <summary>
/// A fresh tenant database on the real TerraBase fixture, provisioned with the infrastructure
/// collections and the key-ring and attempt-limit collections. Several servers bound to one
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
        // ProvisionAsync already creates the collections readiness requires, the key ring's among them.
        foreach (var collection in new[] { KeyRing.Collection, AttemptLimiter.Collection }.Except(AuthStore.RequiredCollections))
        {
            await database.CreateCollectionAsync(collection);
        }

        return new SharedStateTarget(fixture, tenant, database);
    }

    /// <summary>Points a server's settings at this database.</summary>
    public void Bind(Dictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values["terrascale-api-url"] = this.fixture.Server.ClientAddress.GetLeftPart(UriPartial.Authority);
        values["terrascale-api-key"] = this.tenant.ApiKey;
        values["database"] = $"{this.tenant.Project.Id}/auth";
    }

    /// <summary>Trusts the fixture's TLS certificate for the server's storage client.</summary>
    public void Register(IServiceCollection services) => services.AddSingleton(this.fixture.Server.Http);

    /// <summary>A stored document, read directly from TerraBase, or <see langword="null"/>.</summary>
    public async Task<JsonElement?> ReadAsync(string collection, string instanceId, string key) =>
        (await this.Database.GetDocumentAsync(collection, AuthStore.NaturalKeyId(instanceId, collection, key)))?.Document;

    /// <summary>Overwrites a document directly in TerraBase, like an operator or attacker with storage access.</summary>
    public async Task PutAsync(string collection, string instanceId, string key, JsonElement document)
    {
        var id = AuthStore.NaturalKeyId(instanceId, collection, key);
        var current = await this.Database.GetDocumentAsync(collection, id);
        await this.Database.PutDocumentAsync(collection, id, document, current is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(current.Version));
    }

    /// <summary>How many documents a collection holds.</summary>
    public async Task<int> CountAsync(string collection) => (await this.Database.ListDocumentsAsync(collection)).Items.Count;
}

/// <summary>A store whose chosen operation fails, by default like an unreachable authority (a timeout).</summary>
/// <param name="inner">The real store that serves every other operation.</param>
/// <param name="failing">The failing operation: <c>get</c>, <c>create</c> or <c>replace</c>.</param>
/// <param name="error">The failure to throw; a <see cref="TimeoutException"/> when omitted.</param>
public sealed class FailingDocumentStore(IDocumentStore inner, string failing, Func<Exception>? error = null) : IDocumentStore
{
    private Exception Failure => error?.Invoke() ?? new TimeoutException();

    /// <inheritdoc />
    public Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken) =>
        failing == "get" ? throw this.Failure : inner.GetAsync<T>(collection, instanceId, key, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken) =>
        failing == "create" ? throw this.Failure : inner.TryCreateAsync(collection, instanceId, key, document, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken) =>
        failing == "replace" ? throw this.Failure : inner.TryReplaceAsync(collection, instanceId, key, document, version, cancellationToken);
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
