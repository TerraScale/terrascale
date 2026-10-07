using System.Buffers.Text;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Cluster;

// TS-633: on gru-1 the terrabase role may publish only to its KV bucket, its stream info and its message gets. Listing the
// members through a key watcher needed a consumer the broker refused, so no member ever held its lease and nothing shipped.
public sealed class NatsMemberStoreTests
{
    [Test]
    public async Task A_member_holds_its_lease_and_ships_with_only_the_terrabase_role_grant()
    {
        await using var nats = new FakeNats();
        using var oci = new FakeOci();
        // Two former members: one left (its key deleted) and one expired (only the bucket's delete marker remains).
        nats.Store("member." + Base64Url.EncodeToString("left"u8), "NATS/1.0\r\nKV-Operation: DEL\r\n\r\n", []);
        nats.Store("member." + Base64Url.EncodeToString("expired"u8), "NATS/1.0\r\nNats-Marker-Reason: MaxAge\r\n\r\n", []);
        var directory = Directory.CreateTempSubdirectory("terrabase-nats-").FullName;
        var credentials = Path.Combine(directory, "nats.creds");
        await File.WriteAllTextAsync(credentials,
            "-----BEGIN NATS USER JWT-----\nunused\n------END NATS USER JWT------\n\n-----BEGIN USER NKEY SEED-----\nunused\n------END USER NKEY SEED------\n");
        try
        {
            await using var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                oci.Configure(settings);
                settings["nats-url"] = nats.Url;
                settings["nats-credentials-file"] = credentials;
            }, oci.Route);

            // The only member is every file's primary: it ships the system file once its first term began (the lease fence).
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var status = JsonDocument.Parse(await server.Client.GetStringAsync(new Uri("/v1/backup", UriKind.Relative), token));
                return status.RootElement.GetProperty("files").EnumerateArray()
                    .Any(file => file.GetProperty("shipping").GetBoolean() && file.GetProperty("lastShippedAt").ValueKind == JsonValueKind.String);
            }, TimeSpan.FromSeconds(60));

            await Assert.That(nats.Violations).IsEqualTo(0);
            await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith("v1/_system/", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
