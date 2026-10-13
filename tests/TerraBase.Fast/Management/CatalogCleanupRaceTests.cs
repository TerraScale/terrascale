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
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_registration_arriving_during_abandonment_preserves_the_registered_copy(bool afterAbandonCheck)
    {
        var hook = new UsageCommitHook();
        var prepared = new TaskCompletionSource<(TenantContext Storage, UsageCommitHook.Gate Gate)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            hook.Install(services);
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
            {
                if (step == "create.files")
                {
                    prepared.SetResult((storage, hook.PauseWrite()));
                }
            });
        });
        var organization = await api.CreateOrganizationAsync("cleanup race");
        var project = await api.CreateProjectAsync(organization, "cleanup race");
        var database = EntityIds.New().ToString();
        using var service = api.Service(api.User);
        var submitting = service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
        {
            id = EntityIds.New().ToString(),
            ops = new object[]
            {
                new { op = "create_organization", id = organization, name = "conflicting organization" },
                new { op = "create_database", organization, project, id = database, name = "main", replica_regions = Replicas },
            },
        });
        var (storage, gate) = await prepared.Task.WaitAsync(TestContext.Current!.Execution.CancellationToken);
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
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await response.Content.ReadAsStringAsync());
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
