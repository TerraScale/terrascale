using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Primitives;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Fast.Backup;

public sealed class RestoreRecoveryTests
{
    [Test]
    public async Task A_restore_left_running_by_a_stopped_process_fails_interrupted_and_names_its_half_created_target(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        var server = host.Api.Server;
        var region = server.Services.GetRequiredService<TerraBaseOptions>().Region;
        var node = server.Services.GetRequiredService<TerraBaseOptions>().MachineId;
        var source = host.Database;
        var started = server.Services.GetRequiredService<TimeProvider>().GetUtcNow().AddHours(-1);

        var operation = new RestoreOperationRecord(
            EntityIds.New().ToString(), source.Organization, source.Project, source.Id, null,
            RestoreKind.Backup, RestoreOperationState.Running, "stranded", null, null, started, null, region, node);
        var target = new ManagedDatabase(EntityIds.New().ToString(), source.Project, "stranded", source.Id, started.AddMinutes(1));

        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
        await using (var write = await store.BeginTransactionAsync(write: true, cancellationToken))
        {
            await CatalogTables.PutDatabaseAsync(write, target, cancellationToken);
            await PutAsync(write, RestoreCatalog.Kind, operation.Id, operation, ManagementJsonContext.Default.RestoreOperationRecord, cancellationToken);
            await write.CommitAsync(cancellationToken);
        }

        await server.Services.GetRequiredService<IManagedBackupService>().RecoverInterruptedRestoresAsync(cancellationToken);

        using var status = await host.Management.GetAsync(ManagementApi.Uri($"{source.Path}/restores/{operation.Id}"), cancellationToken);
        var settled = await status.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(status.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(settled.GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(settled.GetProperty("errorCode").GetString()).IsEqualTo("interrupted");
        await Assert.That(settled.GetProperty("errorMessage").GetString())
            .IsEqualTo($"the process stopped while the restore was running; database stranded ({target.Id}) may hold a partial or complete restore — check it and delete it if unwanted");

        await using var read = await store.BeginTransactionAsync(write: false, cancellationToken);
        await Assert.That(await CatalogTables.DatabaseAsync(read, source.Project, target.Id, cancellationToken)).IsNotNull();
    }
}
