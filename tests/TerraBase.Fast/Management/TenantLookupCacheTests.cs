using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Fast.Storage;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class TenantLookupCacheTests
{
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";
    private static readonly string[] InSfo = ["sfo-1"];

    [Test]
    public async Task Identity_lookups_follow_committed_registration_placement_and_deletion()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var directory = Directory.CreateTempSubdirectory("terrabase-lookup-cache-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "gru-1";
                settings["node-name"] = "gru-member";
                settings["data-directory"] = Directory.CreateDirectory(Path.Combine(directory, "gru")).FullName;
            }, services =>
            {
                // A cell places a new database in every other serving region (decision 0013): sfo-1 serves too.
                var members = new FakeMemberStore();
                members.Join(new ClusterMember("sfo-member", string.Empty, "sfo-1", string.Empty));
                services.AddSingleton<IMemberDirectory>(members);
            });
            var tenants = gru.Server.Services.GetRequiredService<ITenantDirectory>();
            var organization = await gru.CreateOrganizationAsync(faker.Company.CompanyName());
            var project = await gru.CreateProjectAsync(organization, faker.Random.AlphaNumeric(12));
            var name = "cached";
            var database = new TenantContext(organization, PlanTier.Hobby, project, EntityIds.New().ToString());
            var path = $"/v1/management/organizations/{organization}/projects/{project}/databases";

            // Negative answers for an id the catalog does not record yet.
            await AssertLookupsAsync(tenants, database, registered: false, token);
            await Assert.That(await tenants.FindDatabaseAsync(organization, project, name, token) is NotFound).IsTrue();

            using (var created = await gru.Client.PostAsJsonAsync(ManagementApi.Uri(path), new { name, replicaRegions = InSfo }, token))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
                database = database with { DatabaseId = (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()! };
            }

            var placed = RequireRecord(await AssertLookupsAsync(tenants, database, registered: true, token));
            await Assert.That(placed.Name).IsEqualTo(name);
            await Assert.That(placed.Placement.Home).IsEqualTo("gru-1");
            await Assert.That(placed.Placement.Replicas).IsEquivalentTo(InSfo);
            await Assert.That(await tenants.FindDatabaseAsync(organization, project, name, token) is TenantContext found ? found.DatabaseId : null).IsEqualTo(database.DatabaseId);

            using (var unplaced = await gru.Client.PatchAsJsonAsync(ManagementApi.Uri($"{path}/{database.DatabaseId}"), new { replicaRegions = Array.Empty<string>() }, token))
            {
                await Assert.That(unplaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            var moved = RequireRecord(await AssertLookupsAsync(tenants, database, registered: true, token));
            await Assert.That(moved.Placement.Replicas).IsEmpty();
            await Assert.That(moved.Placement.Version).IsGreaterThan(placed.Placement.Version);

            using (var deleted = await gru.Client.DeleteAsync(ManagementApi.Uri($"{path}/{database.DatabaseId}"), token))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            await AssertLookupsAsync(tenants, database, registered: false, token);
            await Assert.That(await tenants.FindDatabaseAsync(organization, project, name, token) is NotFound).IsTrue();

            // The name binds a new id; the deleted one stays unknown.
            var recreated = await gru.CreateDatabaseAsync(organization, project, name);
            await Assert.That(await tenants.FindDatabaseAsync(organization, project, name, token) is TenantContext recreatedDatabase ? recreatedDatabase.DatabaseId : null).IsEqualTo(recreated);
            await AssertLookupsAsync(tenants, database, registered: false, token);
            await AssertLookupsAsync(tenants, database with { DatabaseId = recreated }, registered: true, token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Cached_identity_lookups_answer_while_the_only_storage_thread_is_held()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        // One Turso thread; the frozen clock keeps periodic catalog work from committing between the reads.
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "2";
        }, StorageTransactionTests.ConfigurePool, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var created = await api.CreateManagedDatabaseAsync();
        var database = new TenantContext(created.Organization, PlanTier.Hobby, created.Project, created.Id);
        var tenants = api.Server.Services.GetRequiredService<ITenantDirectory>();
        var record = await AssertLookupsAsync(tenants, database, registered: true, token);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();

        await using (await StorageFixture.HoldWorkerAsync(engine, token))
        {
            var registered = tenants.RegisteredAsync(database, token).AsTask();
            await Assert.That(registered.IsCompletedSuccessfully).IsTrue();
            await Assert.That(await registered is bool known && known).IsTrue();
            var placement = tenants.DatabaseRecordAsync(database, token).AsTask();
            await Assert.That(placement.IsCompletedSuccessfully).IsTrue();
            await Assert.That(Describe(await placement)).IsEqualTo(Describe(record));
            // Still held: a catalog read could only have completed once the fixture gave the thread up.
            await Assert.That(engine.Io.Running).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Oversized_remote_placements_are_answered_without_retaining_cached_records(bool oversizedRegion)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "2";
        }, StorageTransactionTests.ConfigurePool, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var created = await api.CreateManagedDatabaseAsync();
        using var response = await api.Client.GetAsync(ManagementApi.Uri(created.Path), token);
        response.EnsureSuccessStatusCode();
        var record = (await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.ManagedDatabase, token))!;
        string[] replicas = oversizedRegion ? [new string('r', 64)] : [.. Enumerable.Repeat("sfo-1", 9)];
        var imported = record with { ReplicaRegions = replicas };
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var system = await engine.OpenSystemDatabaseAsync(token);
        // WAN import can persist a larger placement than local management accepts.
        await using (var transaction = await system.BeginTransactionAsync(write: true, token))
        {
            await transaction.PutAsync($"projects/{created.Project}/databases", created.Id,
                JsonSerializer.SerializeToUtf8Bytes(imported, TerraBaseJsonContext.Default.ManagedDatabase), token);
            await transaction.CommitAsync(token);
        }

        var database = new TenantContext(created.Organization, PlanTier.Hobby, created.Project, created.Id);
        var tenants = api.Server.Services.GetRequiredService<ITenantDirectory>();
        var management = api.Server.Services.GetRequiredService<IManagementService>();
        await Assert.That(RequireRecord(await tenants.DatabaseRecordAsync(database, token)).Placement.Replicas).IsEquivalentTo(replicas);
        await Assert.That(RequireManagedRecord(await management.FindDatabaseRecordAsync(created.Organization, created.Project, created.Name, token)).Replicas).IsEquivalentTo(replicas);

        var turso = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        Task<DatabaseRecordResult> placement;
        await using (await StorageFixture.HoldWorkerAsync(turso, token))
        {
            placement = tenants.DatabaseRecordAsync(database, token).AsTask();
            await Assert.That(placement.IsCompleted).IsFalse();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(turso.Io.Queued > 0 && !placement.IsCompleted));
        }
        await Assert.That(RequireRecord(await placement).Placement.Replicas).IsEquivalentTo(replicas);

        Task<FindDatabaseRecordResult> named;
        await using (await StorageFixture.HoldWorkerAsync(turso, token))
        {
            named = management.FindDatabaseRecordAsync(created.Organization, created.Project, created.Name, token).AsTask();
            await Assert.That(named.IsCompleted).IsFalse();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(turso.Io.Queued > 0 && !named.IsCompleted));
        }
        await Assert.That(RequireManagedRecord(await named).Replicas).IsEquivalentTo(replicas);
        using var current = await api.Client.GetAsync(ManagementApi.Uri(created.Path), token);
        current.EnsureSuccessStatusCode();
        await Assert.That((await current.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.ManagedDatabase, token))!.Replicas).IsEquivalentTo(replicas);
    }

    [Test]
    [Arguments("valid")]
    [Arguments("long-name")]
    [Arguments("invalid-name")]
    [Arguments("long-project")]
    [Arguments("invalid-project")]
    public async Task Missing_route_records_are_cached_only_with_bounded_valid_keys(string scenario)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "2";
        }, StorageTransactionTests.ConfigurePool, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var created = await api.CreateManagedDatabaseAsync();
        var project = scenario switch
        {
            "long-project" => new string('p', 128),
            "invalid-project" => "invalid-project",
            _ => created.Project,
        };
        var name = scenario switch
        {
            "long-name" => new string('n', 128),
            "invalid-name" => "INVALID",
            _ => "missing",
        };
        var missing = created with { Project = project, Name = name };
        var expected = scenario == "valid" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest;
        using var client = await api.CreateClientAsync(created.Organization, "documents:read");
        using (var response = await client.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(missing, "items", "absent")), token))
        {
            await Assert.That(response.StatusCode).IsEqualTo(expected);
        }

        var management = api.Server.Services.GetRequiredService<IManagementService>();
        await Assert.That(await management.FindDatabaseRecordAsync(created.Organization, project, name, token) is NotFound).IsTrue();
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        Task<FindDatabaseRecordResult> lookup;
        await using (await StorageFixture.HoldWorkerAsync(engine, token))
        {
            lookup = management.FindDatabaseRecordAsync(created.Organization, project, name, token).AsTask();
            if (scenario == "valid")
            {
                await Assert.That(lookup.IsCompletedSuccessfully).IsTrue();
            }
            else
            {
                await Assert.That(lookup.IsCompleted).IsFalse();
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Queued > 0 && !lookup.IsCompleted));
            }
        }

        await Assert.That(await lookup is NotFound).IsTrue();
        using var current = await client.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(missing, "items", "absent")), token);
        await Assert.That(current.StatusCode).IsEqualTo(expected);
    }

    // Each lookup twice: the second answers from the cache unless the catalog committed in between.
    private static async Task<DatabaseRecordResult> AssertLookupsAsync(
        ITenantDirectory tenants, TenantContext database, bool registered, CancellationToken token)
    {
        var record = await tenants.DatabaseRecordAsync(database, token);
        await Assert.That(record is TenantDatabaseRecord).IsEqualTo(registered);
        await Assert.That(record is NotFound).IsEqualTo(!registered);
        await Assert.That(Describe(await tenants.DatabaseRecordAsync(database, token))).IsEqualTo(Describe(record));
        await Assert.That(await tenants.RegisteredAsync(database, token) switch
        {
            bool known => known == registered,
            Unavailable => false,
        }).IsTrue();
        await Assert.That(await tenants.RegisteredAsync(database, token) switch
        {
            bool known => known == registered,
            Unavailable => false,
        }).IsTrue();
        return record;
    }

    private static string? Describe(DatabaseRecordResult record) => record switch
    {
        TenantDatabaseRecord value => $"{value.Name} {value.Placement.Home} [{string.Join(',', value.Placement.Replicas)}] {value.Placement.Version}",
        NotFound => null,
        Unavailable => throw new InvalidOperationException("the catalog fixture is unavailable"),
    };

    private static TenantDatabaseRecord RequireRecord(DatabaseRecordResult record) => record switch
    {
        TenantDatabaseRecord value => value,
        _ => throw new InvalidOperationException("the catalog fixture database was not found"),
    };

    private static ManagedDatabase RequireManagedRecord(FindDatabaseRecordResult record) => record switch
    {
        ManagedDatabase value => value,
        _ => throw new InvalidOperationException("the catalog fixture database was not found"),
    };
}
