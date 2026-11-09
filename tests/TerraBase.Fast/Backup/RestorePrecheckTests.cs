using System.Net;

namespace TerraBase.Fast.Backup;

public sealed class RestorePrecheckTests
{
    [Test]
    public async Task A_point_in_time_restore_into_a_taken_name_is_refused_before_any_replay(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        await host.OkAsync("CREATE TABLE items (id TEXT PRIMARY KEY)");
        await host.SnapshotAsync(cancellationToken);
        var second = await host.Api.CreateManagedDatabaseAsync(host.Database.Organization, host.Database.Project, "taken");

        var gets = 0;
        host.S3.ObjectRequested += (customer, method, key) =>
        {
            if (!customer && key.StartsWith(host.File + "/", StringComparison.Ordinal) && method == HttpMethod.Get)
            {
                Interlocked.Increment(ref gets);
            }
        };

        using var restored = await host.Management.PostAsJsonAsync(
            ManagementApi.Uri($"{host.Database.Path}/point-in-time/restore"), new { pointInTime = host.SnapshotAt, name = second.Name }, cancellationToken);
        await ErrorAssert.ErrorAsync(restored, HttpStatusCode.Conflict, "name_taken", "name is already taken");

        // The replay never ran: the restore read no object of the database's backup.
        await Assert.That(gets).IsEqualTo(0);
    }
}
