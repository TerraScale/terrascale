using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupAnchorTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Only_replacing_a_backup_destination_requires_an_anchor(bool replacing)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        });
        var database = await api.CreateManagedDatabaseAsync();
        var route = ManagementApi.Uri(database.ProjectPath + "/backup-destination");
        if (replacing)
        {
            using var initial = await api.Client.PutAsJsonAsync(route, Destination("customer"), token);
            initial.EnsureSuccessStatusCode();
        }

        s3.FailPut = key => key.StartsWith("catalog-anchor/", StringComparison.Ordinal);
        using var saved = await api.Client.PutAsJsonAsync(route, Destination("both"), token);
        await Assert.That(saved.StatusCode).IsEqualTo(replacing ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        if (replacing)
        {
            var error = await saved.Content.ReadFromJsonAsync<JsonElement>(token);
            await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("catalog_anchor_unavailable");
            s3.FailPut = _ => false;
            using var retried = await api.Client.PutAsJsonAsync(route, Destination("both"), token);
            retried.EnsureSuccessStatusCode();
            await Assert.That(s3.Objects.ContainsKey("catalog-anchor/local")).IsTrue();
        }
    }

    private static object Destination(string mode) => new
    {
        endpoint = FakeS3.CustomerEndpoint,
        region = "test-region-1",
        bucket = FakeS3.CustomerBucket,
        accessKeyId = FakeS3.CustomerAccessKey,
        secretKey = FakeS3.CustomerSecretKey,
        mode,
    };
}
