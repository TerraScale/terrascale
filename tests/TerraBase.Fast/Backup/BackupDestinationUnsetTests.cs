using System.Net;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Backup;

public sealed class BackupDestinationUnsetTests
{
    [Test]
    public async Task Reading_a_destination_that_was_never_set_says_none_is_set()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await StartAsync();
        var database = await api.CreateManagedDatabaseAsync();

        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(database.ProjectPath + "/backup-destination"), token), HttpStatusCode.NotFound, "not_found",
            "no backup destination is set for this project; set one with setBackupDestination");
    }
}
