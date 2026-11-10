using System.Net;
using System.Text.Json;

namespace TerraBase.Fast.Backup;

public sealed class ManagedBackupUploadTests
{
    [Test]
    public async Task A_snapshot_stores_its_copy_and_fails_when_that_upload_fails(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        using var created = await host.Management.PostAsync(ManagementApi.Uri($"{host.Database.Path}/backups"), content: null, cancellationToken);
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString();
        var key = $"v1/{host.Database.Organization}/{host.Database.Project}/{backupId}/managed-backup";
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(host.S3.Objects.ContainsKey(key)).IsTrue();

        host.S3.FailPut = uploaded => uploaded.EndsWith("/managed-backup", StringComparison.Ordinal);
        using var failed = await host.Management.PostAsync(ManagementApi.Uri($"{host.Database.Path}/backups"), content: null, cancellationToken);
        await Assert.That(failed.StatusCode).IsNotEqualTo(HttpStatusCode.Created);
        await Assert.That(host.S3.Objects.Keys.Count(stored => stored.EndsWith("/managed-backup", StringComparison.Ordinal))).IsEqualTo(1);
    }
}
