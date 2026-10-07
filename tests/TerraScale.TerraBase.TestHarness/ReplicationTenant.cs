using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Testing;

// The system catalog is local to each node. Tests provision the same identity explicitly before replicating tenant data.
internal sealed class ReplicationTenant
{
    private const int MaxDatabases = 32;
    private const int MaxCatalogBytes = 1024 * 1024;

    private readonly Dictionary<string, TenantContext> _databases;
    private readonly List<(string Collection, StoredDocument Document)> _catalog;

    private ReplicationTenant(string userId, string organizationId, string projectId, Dictionary<string, TenantContext> databases,
        List<(string Collection, StoredDocument Document)> catalog)
    {
        UserId = userId;
        OrganizationId = organizationId;
        ProjectId = projectId;
        _databases = databases;
        _catalog = catalog;
    }

    public string UserId { get; }

    public string OrganizationId { get; }

    public string ProjectId { get; }

    public TenantContext Database(string name) => _databases[name];

    public string Path(string name) => $"/v1/projects/{ProjectId}/databases/{name}";

    public string File(TerraBaseTestServer server, string name) =>
        System.IO.Path.Combine(server.DataDirectory, OrganizationId, ProjectId, Database(name).DatabaseId + ".db");

    public Task<string> KeyAsync(TerraBaseTestServer server, params string[] scopes) => server.IssueAsync(OrganizationId, scopes);

    public static async Task<ReplicationTenant> CreateAsync(TerraBaseTestServer seed, params string[] databaseNames)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(databaseNames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(databaseNames.Length, MaxDatabases);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var user = Guid.NewGuid().ToString("D");
        var management = seed.Services.GetRequiredService<IManagementService>();
        var organization = (await management.CreateOrganizationAsync(user, "replication", token)).Value
            ?? throw new InvalidOperationException("could not create the replication organization");
        var project = (await management.CreateProjectAsync(user, organization.Id, "replication", token)).Value
            ?? throw new InvalidOperationException("could not create the replication project");
        var directory = seed.Services.GetRequiredService<ITenantDirectory>();
        var databases = new Dictionary<string, TenantContext>(StringComparer.Ordinal);
        foreach (var name in databaseNames)
        {
            var created = await directory.CreateDatabaseAsync(organization.Id, project.Id, name, token)
                ?? throw new InvalidOperationException("could not create the replication database");
            databases.Add(name, created.Database);
        }

        // Exact keys bound this capture to seven shared rows plus two per named database; API keys are never copied.
        var keys = new List<(string Collection, string Id)>
        {
            ("catalog", "version"),
            ("organizations", organization.Id),
            ($"users/{user}/organization_names", "replication"),
            ($"users/{user}/organizations", organization.Id),
            ($"organizations/{organization.Id}/members", user),
            ($"organizations/{organization.Id}/projects", project.Id),
            ($"organizations/{organization.Id}/project_names", "replication"),
        };
        foreach (var (name, database) in databases)
        {
            keys.Add(($"projects/{project.Id}/databases", database.DatabaseId!));
            keys.Add(($"projects/{project.Id}/database_names", name));
        }

        var store = await seed.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        var catalog = new List<(string Collection, StoredDocument Document)>(keys.Count);
        var bytes = 0;
        foreach (var (collection, id) in keys)
        {
            var document = await read.GetAsync(collection, id, token)
                ?? throw new InvalidOperationException("the replication fixture catalog is incomplete");
            bytes += document.Json.Length;
            if (bytes > MaxCatalogBytes)
            {
                throw new InvalidOperationException("the replication fixture catalog exceeds its byte limit");
            }

            catalog.Add((collection, document));
        }

        return new ReplicationTenant(user, organization.Id, project.Id, databases, catalog);
    }

    public async Task CaptureDatabaseAsync(TerraBaseTestServer seed, string name)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_databases.Count + 1, MaxDatabases);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var database = await seed.Services.GetRequiredService<ITenantDirectory>()
            .FindDatabaseAsync(OrganizationId, ProjectId, name, deadline.Token)
            ?? throw new InvalidOperationException("the replication fixture database does not exist");
        var store = await seed.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(deadline.Token);
        await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
        var collection = $"projects/{ProjectId}/databases";
        var names = $"projects/{ProjectId}/database_names";
        var document = await read.GetAsync(collection, database.DatabaseId!, deadline.Token)
            ?? throw new InvalidOperationException("the replication fixture catalog is incomplete");
        var index = await read.GetAsync(names, name, deadline.Token)
            ?? throw new InvalidOperationException("the replication fixture catalog is incomplete");
        if (_catalog.Sum(row => (long)row.Document.Json.Length) + document.Json.Length + index.Json.Length > MaxCatalogBytes)
        {
            throw new InvalidOperationException("the replication fixture catalog exceeds its byte limit");
        }

        _databases.Add(name, database);
        _catalog.Add((collection, document));
        _catalog.Add((names, index));
    }

    public async Task BindAsync(TerraBaseTestServer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var engine = other.Services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(deadline.Token);
        await using (var write = await store.BeginTransactionAsync(write: true, deadline.Token))
        {
            foreach (var (collection, document) in _catalog)
            {
                await write.CreateCollectionAsync(new CollectionInfo(collection, 0), deadline.Token);
                await write.PutAsync(collection, document.Id, document.Json, deadline.Token);
            }

            await write.CommitAsync(deadline.Token);
        }

        foreach (var database in _databases.Values)
        {
            await engine.CreateDatabaseAsync(database, deadline.Token);
        }
    }
}
