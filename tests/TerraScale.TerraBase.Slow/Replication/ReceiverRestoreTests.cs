using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

public sealed class ReceiverRestoreTests
{
    [Test]
    public async Task A_caught_up_sender_repairs_a_restarted_receiver_restored_to_an_older_frontier()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-receiver-restore-").FullName;
        TerraBaseTestServer? receiver = null;
        var port = 0;
        void ConfigureReceiver(Dictionary<string, string> settings)
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["data-directory"] = data;
            settings["peer-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        try
        {
            receiver = await TerraBaseTestServer.StartAsync(ConfigureReceiver);
            port = receiver.PeerAddress!.Port;
            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["peer-addresses"] = receiver.PeerAddress.ToString();
            });
            var tenant = await ReplicationTenant.CreateAsync(sender, "restored");
            await tenant.BindAsync(receiver);
            var path = tenant.Path("restored");
            var databaseFile = tenant.File(receiver, "restored");
            using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
            using (var created = await writer.PutAsync(new Uri(path, UriKind.Relative), null))
            {
                created.EnsureSuccessStatusCode();
            }

            using (var collection = await writer.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" }))
            {
                collection.EnsureSuccessStatusCode();
            }

            await PutAsync(writer, path, "a");
            await TerraBaseTestServer.ReplicatedAsync(sender);
            var snapshot = Path.Combine(data, "older.snapshot");
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $path;";
                vacuum.Parameters.AddWithValue("$path", snapshot);
                await vacuum.ExecuteNonQueryAsync();
            }

            await PutAsync(writer, path, "b");
            await TerraBaseTestServer.ReplicatedAsync(sender);
            var sent = await sender.MetricAsync("terrabase_replication_sent_groups_total");
            var database = tenant.Database("restored");
            IDatabaseStore? source;
            long sourceHead;
            using (var sourceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                source = await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, sourceDeadline.Token);
                await using var initialSourceRead = await source!.BeginTransactionAsync(write: false, sourceDeadline.Token);
                sourceHead = await initialSourceRead.GetLastChangeSequenceAsync(sourceDeadline.Token);
            }

            var old = await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None);
            var incarnation = old!.Incarnation;
            await receiver.DisposeAsync();
            receiver = null;
            File.Copy(snapshot, databaseFile, overwrite: true);
            receiver = await TerraBaseTestServer.StartAsync(ConfigureReceiver);
            var restored = await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None);
            await Assert.That(restored!.Incarnation).IsEqualTo(incarnation);

            // Old zero-lag ACKs cannot satisfy the barrier: the idle sender must first replay the missing group.
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await sender.MetricAsync("terrabase_replication_sent_groups_total", "", token) > sent);
            await TerraBaseTestServer.ReplicatedAsync(sender);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
            foreach (var id in new[] { "a", "b" })
            {
                using var response = await reader.GetAsync(new Uri($"{path}/collections/items/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative), deadline.Token);
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                await Assert.That(body.RootElement.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(id);
            }

            await using var sourceRead = await source!.BeginTransactionAsync(write: false, deadline.Token);
            await using var targetRead = await restored.BeginTransactionAsync(write: false, deadline.Token);
            await Assert.That(await sourceRead.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(sourceHead);
            await Assert.That(await targetRead.GetAppliedSequenceAsync(await sourceRead.GetOriginAsync(deadline.Token), deadline.Token)).IsEqualTo(sourceHead);
            await Assert.That(await sender.MetricAsync("terrabase_replication_sent_groups_total")).IsEqualTo(sent + 1);
        }
        finally
        {
            if (receiver is not null)
            {
                await receiver.DisposeAsync();
            }

            Directory.Delete(data, recursive: true);
        }
    }

    private static async Task PutAsync(HttpClient writer, string path, string id)
    {
        using var response = await writer.PutAsJsonAsync(new Uri($"{path}/collections/items/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative), new { value = id });
        response.EnsureSuccessStatusCode();
    }
}
