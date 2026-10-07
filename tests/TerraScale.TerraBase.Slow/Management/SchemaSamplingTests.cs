using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class SchemaSamplingTests
{
    [Test]
    public async Task Sampling_stops_after_the_first_1000_documents_in_id_order()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        // Ids of zero-padded names sort d0000…d1000, so only the last document, which alone has late, falls outside the sample.
        await ManagementApi.CreateCollectionAsync(data, database, "many");
        for (var i = 0; i <= 1000; i++)
        {
            object document = i < 1000 ? new { early = i } : new { early = i, late = true };
            (await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "many", $"d{i:D4}")), document)).EnsureSuccessStatusCode();
        }

        using var schema = JsonDocument.Parse(await api.Client.GetStringAsync(ManagementApi.Uri($"{database.Path}/collections/many/schema")));

        var root = schema.RootElement;
        await Assert.That(root.GetProperty("documents").GetInt64()).IsEqualTo(1001);
        await Assert.That(root.GetProperty("sampledDocuments").GetInt32()).IsEqualTo(1000);
        await Assert.That(root.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("path").GetString()!)).IsEquivalentTo(["early"]);
        await Assert.That(root.GetProperty("fields")[0].GetProperty("present").GetInt32()).IsEqualTo(1000);
    }
}
