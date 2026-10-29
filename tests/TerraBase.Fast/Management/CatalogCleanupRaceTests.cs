using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class CatalogCleanupRaceTests
{
    private static readonly string[] Replicas = ["sjc"];

    [Test]
    public async Task Startup_preserves_a_registered_database_with_a_pending_shared_creation_intent()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-shared-create-intent-").FullName;
        var user = EntityIds.New().ToString();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            ManagedDatabaseRef database;
            TenantContext tenant;
            Ulid incarnation;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: time, userId: user))
            {
                database = await api.CreateManagedDatabaseAsync();
                tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
                var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
                incarnation = (await engine.OpenDatabaseAsync(tenant, token))!.Incarnation;
                var catalog = await engine.OpenSystemDatabaseAsync(token);
                await using var write = await catalog.BeginTransactionAsync(write: true, token);
                // A peer's snapshot may precede clearing its creation intent while another peer has the registration.
                _ = await write.PutAsync("pending_database_cleanup", database.Id,
                    JsonSerializer.SerializeToUtf8Bytes(tenant, TerraBaseJsonContext.Default.TenantContext), token);
                _ = await write.CommitAsync(token);
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: time, userId: user);
            var storage = restarted.Server.Services.GetRequiredService<IStorageEngine>();
            await Assert.That((await storage.OpenDatabaseAsync(tenant, token))!.Incarnation).IsEqualTo(incarnation);
            var system = await storage.OpenSystemDatabaseAsync(token);
            await using (var read = await system.BeginTransactionAsync(write: false, token))
            {
                await Assert.That(await read.GetAsync($"projects/{database.Project}/databases", database.Id, token)).IsNotNull();
                await Assert.That(await read.GetAsync("pending_database_cleanup", database.Id, token)).IsNotNull();
            }

            // A subsequent real deletion still owns and clears the intent; startup never certified any absence.
            using var removed = await restarted.Client.DeleteAsync(ManagementApi.Uri(
                $"/v1/management/organizations/{database.Organization}/projects/{database.Project}/databases/{database.Id}"), token);
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(await storage.OpenDatabaseAsync(tenant, token)).IsNull();
            await using var final = await system.BeginTransactionAsync(write: false, token);
            await Assert.That(await final.GetAsync("pending_database_cleanup", database.Id, token)).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_registration_arriving_during_abandonment_preserves_the_registered_copy(bool afterAbandonCheck)
    {
        var hook = new UsageCommitHook();
        var prepared = new TaskCompletionSource<(TenantContext Storage, UsageCommitHook.Gate Gate)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            new OtherRegions("sjc").Register(services);
            hook.Install(services);
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
            {
                if (step == "create.files")
                {
                    prepared.SetResult((storage, hook.PauseWrite(fail: true)));
                }
            });
        });
        var organization = await api.CreateOrganizationAsync("cleanup race");
        var project = await api.CreateProjectAsync(organization, "cleanup race");
        var submitting = api.Client.PostAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"),
            new { name = "main", replicaRegions = Replicas });
        var (storage, gate) = await prepared.Task.WaitAsync(TestContext.Current!.Execution.CancellationToken);
        var database = storage.DatabaseId!;
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var catalog = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        var checkedRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDatabaseStore copy;
        using (gate)
        {
            await gate.Entered.Task.WaitAsync(TestContext.Current.Execution.CancellationToken);
            copy = await engine.OpenDatabaseAsync(storage, CancellationToken.None) ?? throw new InvalidOperationException("the prepared copy is missing");
            await using (var write = await copy.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                await write.CreateCollectionAsync(new CollectionInfo("items", 0), CancellationToken.None);
                await write.PutAsync("items", database, "{\"value\":42}"u8.ToArray(), CancellationToken.None);
                await write.CommitAsync(CancellationToken.None);
            }

            if (afterAbandonCheck)
            {
                // Hold the absent registration captured by abandonment's first catalog snapshot.
                hook.AfterGet = async (collection, id, token) =>
                {
                    if (collection == $"projects/{project}/databases" && id == database)
                    {
                        hook.AfterGet = null;
                        checkedRegistration.SetResult();
                        await resume.Task.WaitAsync(token);
                    }
                };
            }
            else
            {
                await RegisterCopyAsync();
            }
        }

        try
        {
            if (afterAbandonCheck)
            {
                await checkedRegistration.Task.WaitAsync(TestContext.Current.Execution.CancellationToken);
                await RegisterCopyAsync();
            }
        }
        finally
        {
            resume.TrySetResult();
        }

        using var response = await submitting;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError).Because(await response.Content.ReadAsStringAsync());
        await Assert.That((await engine.OpenDatabaseAsync(storage, CancellationToken.None))!.Incarnation).IsEqualTo(copy.Incarnation);
        await using (var data = await copy.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That((await data.GetAsync("items", database, CancellationToken.None))!.Json.ToArray()).IsEquivalentTo("{\"value\":42}"u8.ToArray());
        }

        await using var read = await catalog.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await read.GetAsync($"projects/{project}/databases", database, CancellationToken.None)).IsNotNull();
        await Assert.That(await read.GetAsync("pending_database_cleanup", database, CancellationToken.None)).IsNull();

        async Task RegisterCopyAsync()
        {
            await using var transaction = await catalog.BeginTransactionAsync(write: true, CancellationToken.None);
            await transaction.PutAsync($"projects/{project}/databases", database,
                JsonSerializer.SerializeToUtf8Bytes(new ManagedDatabase(database, project, "main", null, DateTimeOffset.UtcNow),
                    TerraBaseJsonContext.Default.ManagedDatabase), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
    }
}
