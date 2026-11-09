using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;
using Turso;

namespace TerraBase.Fast.Sync;

public sealed class ReplicaAntiEntropyTests
{
    private readonly Faker _faker = new();

    [Test]
    public async Task AHealthyReplicaVerifiesEqualAndATamperedOneIsDetectedAndBootstrappedAgain()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var directory = Directory.CreateTempSubdirectory("terrabase-anti-entropy-").FullName;
        try
        {
            var database = await server.CreateSyncedDatabaseAsync();
            await server.ExecuteAsync(database, "INSERT INTO items(id, tenant, v) VALUES "
                + string.Join(", ", Enumerable.Range(0, 600).Select(i => $"('r{i:D4}', '{database.DatabaseId}', '{_faker.Random.AlphaNumeric(40)}')")));
            var options = server.Services.GetRequiredService<TerraBaseOptions>() with { DataDirectory = directory };
            var source = server.PullAddress(database);
            using var http = PeerTls.CreateClient(server.BaseAddress);

            await using (var replicas = new TursoReplicas(options, new TestMasterKey(), server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance))
            {
                await replicas.OpenAsync(database, source, http, token);
                var verifier = new ReplicaVerifier(replicas, http);
                await Assert.That(await verifier.RoundAsync(database, source, null, token)).IsEqualTo(new VerificationRound(ReplicaVerdict.Equal, null, null));

                await server.ExecuteAsync(database, "UPDATE items SET v = 'moved' WHERE id = 'r0300'");
                await Assert.That((await verifier.ObserveAsync(database, source, null, token)).Verdict).IsEqualTo(ReplicaVerdict.Moved);
                await Assert.That(await verifier.RoundAsync(database, source, null, token)).IsEqualTo(new VerificationRound(ReplicaVerdict.Equal, null, null));
            }

            await TamperAsync(directory, database, source, http);

            await using var reopened = new TursoReplicas(options, new TestMasterKey(), server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
            await reopened.OpenAsync(database, source, http, token);
            var healing = new ReplicaVerifier(reopened, http);
            var diverged = await healing.RoundAsync(database, source, null, token);
            await Assert.That(diverged.Verdict).IsEqualTo(ReplicaVerdict.Diverged);
            await Assert.That(diverged.Where).Contains("256 rows and");

            await healing.HealAsync(database, source, token);
            await Assert.That(await healing.RoundAsync(database, source, null, token)).IsEqualTo(new VerificationRound(ReplicaVerdict.Equal, null, null));
            await using var reader = (await reopened.BeginReadAsync(database, token))!;
            var rows = await reader.ExecuteAsync(new SqlStatement("SELECT count(*), sum(v = 'moved') FROM items", [], new Dictionary<string, object?>(), 1, 1024, TimeSpan.FromSeconds(5)), token);
            await Assert.That(rows.Rows.Single()).IsEquivalentTo(new object?[] { 600L, 1L });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task TamperAsync(string directory, TenantContext database, Uri source, HttpClient http)
    {
        var path = Path.Combine(directory, TursoReplicas.DirectoryName, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");
        var record = await File.ReadAllTextAsync(path + DatabaseKeys.Suffix, Encoding.ASCII);
        var key = await new TestMasterKey().UnwrapAsync(record[TursoReplicas.KeyHeader.Length..^1], CancellationToken.None);
        using var sync = await TursoSyncDatabase.OpenAsync(new TursoSyncDatabaseOptions(path, source)
        {
            HttpClient = http,
            LocalEncryption = new TursoLocalEncryptionOptions { Cipher = TursoRemoteEncryptionCipher.Aegis256, HexKey = Convert.ToHexString(key) },
        });
        await using var connection = await sync.ConnectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM items WHERE id = 'r0123'";
        await Assert.That(await command.ExecuteNonQueryAsync()).IsEqualTo(1);
    }
}
