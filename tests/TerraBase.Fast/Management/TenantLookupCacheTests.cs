using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Fast.Storage;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class TenantLookupCacheTests
{
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";

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
            });
            var tenants = gru.Server.Services.GetRequiredService<ITenantDirectory>();
            var organization = await gru.CreateOrganizationAsync(faker.Company.CompanyName());
            var project = await gru.CreateProjectAsync(organization, faker.Random.AlphaNumeric(12));
            var name = "cached";
            var database = new TenantContext(organization, PlanTier.Hobby, project, EntityIds.New().ToString());
            var path = $"/v1/management/organizations/{organization}/projects/{project}/databases";

            await AssertLookupsAsync(tenants, database, registered: false, token);
            await Assert.That(await tenants.FindDatabaseAsync(organization, project, name, token) is NotFound).IsTrue();

            using (var created = await gru.Client.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
            {
                id = EntityIds.New().ToString(),
                ops = new[] { new { op = "create_database", organization, project, name, id = database.DatabaseId!, replica_regions = Array.Empty<string>() } },
            }, token))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            var placed = RequireRecord(await AssertLookupsAsync(tenants, database, registered: true, token));
            await Assert.That(placed.Name).IsEqualTo(name);
            await Assert.That(placed.Placement.Home).IsEqualTo("gru-1");
            await Assert.That(placed.Placement.Replicas).IsEmpty();
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

            await Assert.That(engine.Io.Running).IsEqualTo(1);
        }
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

}
