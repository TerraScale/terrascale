using System.Text.Json;
using Bogus;
using Microsoft.Data.Sqlite;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Replication;

public sealed class StartupDiscoveryTests
{
    [Test]
    public async Task An_unusable_persisted_database_does_not_stop_healthy_replication_and_is_retried()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
        });
        var directory = Directory.CreateTempSubdirectory("terrabase-discovery-").FullName;
        try
        {
            ReplicationTenant tenant;
            var value = new Faker().Lorem.Word();
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = directory;
            }))
            {
                tenant = await ReplicationTenant.CreateAsync(seed, "healthy", "broken");
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                foreach (var name in new[] { "healthy", "broken" })
                {
                    using var collection = await writer.PostAsJsonAsync(
                        new Uri($"{tenant.Path(name)}/collections", UriKind.Relative), new { name = "items" }, deadline.Token);
                    collection.EnsureSuccessStatusCode();
                    using var document = await writer.PutAsJsonAsync(Document(tenant, name), new { value }, deadline.Token);
                    document.EnsureSuccessStatusCode();
                }

                await tenant.BindAsync(receiver);
            }

            var file = Path.Combine(directory, tenant.OrganizationId, tenant.ProjectId, tenant.Database("broken").DatabaseId + ".db");
            long supportedVersion;
            await using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                supportedVersion = (long)(await command.ExecuteScalarAsync())!;
                command.CommandText = "PRAGMA user_version = 999;";
                await command.ExecuteNonQueryAsync();
            }

            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = directory;
                settings["peer-addresses"] = receiver.PeerAddress!.ToString();
            });
            using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var response = await reader.GetAsync(Document(tenant, "healthy"), token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return false;
                }

                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return body.RootElement.GetProperty("document").GetProperty("value").GetString() == value;
            });
            using var health = await sender.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
            health.EnsureSuccessStatusCode();
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_initialized")).IsEqualTo(0);

            // SQLite serializes the repair with any concurrent discovery; never overwrite a file being opened.
            await using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA user_version = {supportedVersion};";
                await command.ExecuteNonQueryAsync();
            }
            await TerraBaseTestServer.ReplicatedAsync(sender);
            using var assertionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var recovered = await reader.GetAsync(Document(tenant, "broken"), assertionDeadline.Token);
            recovered.EnsureSuccessStatusCode();
            using var recoveredBody = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync(assertionDeadline.Token));
            await Assert.That(recoveredBody.RootElement.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Uri Document(ReplicationTenant tenant, string name) =>
        new($"{tenant.Path(name)}/collections/items/documents/{ReplicationDocuments.Id("persisted")}", UriKind.Relative);
}
