using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Import;

/// <summary>NDJSON import (docs/terrabase/import-ndjson.md): mongoexport-style documents and a round trip of the export.</summary>
public sealed class NdjsonImportTests
{
    [Test]
    public async Task A_10k_line_mongoexport_file_imports_every_line_under_generated_ulids_with_oids_kept_as_fields()
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var faker = new Faker();
        var text = new StringBuilder();
        for (var i = 0; i < 10_000; i++)
        {
            // Every tenth line has no _id, as a hand-written file might.
            var id = i % 10 == 0 ? "" : $"\"_id\":{{\"$oid\":\"{i:x24}\"}},";
            text.Append('{').Append(id).Append("\"name\":").Append(JsonSerializer.Serialize(faker.Name.FullName()))
                .Append(",\"n\":").Append(i).Append(",\"tags\":[\"a\",\"b\"]}\n");
        }

        var source = Encoding.UTF8.GetBytes(text.ToString());
        var (created, _) = await client.CreateAsync(import, "ndjson", source, new { collection = "users" });
        await Assert.That(created).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await client.UploadAsync(import, source)).Status).IsEqualTo(HttpStatusCode.OK);
        var ready = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready").Because(ready.GetRawText());
        var table = ready.GetProperty("preview").GetProperty("tables")[0];
        await Assert.That(table.GetProperty("collection").GetString()).IsEqualTo("users");
        await Assert.That(table.GetProperty("rows").GetInt64()).IsEqualTo(10_000);

        var (started, _) = await client.StartAsync(import, ready.GetProperty("preview").GetProperty("id").GetString());
        await Assert.That(started).IsEqualTo(HttpStatusCode.Accepted);
        var done = await client.WaitAsync(import, "completed", "failed");
        await Assert.That(done.GetProperty("state").GetString()).IsEqualTo("completed").Because(done.GetRawText());
        await Assert.That(done.GetProperty("progress").GetProperty("rowsWritten").GetInt64()).IsEqualTo(10_000);
        await Assert.That(done.GetProperty("validation").GetProperty("passed").GetBoolean()).IsTrue();

        var ids = await client.IdsAsync(import, "users");
        await Assert.That(ids.Count).IsEqualTo(10_000);
        await Assert.That(ids.All(ImportClient.IsUlid)).IsTrue();
        await Assert.That(ids.Distinct().Count()).IsEqualTo(10_000);
        var kept = await client.DocumentAsync(import, "users", ("n", "7"));
        await Assert.That(kept.GetProperty("_id").GetProperty("$oid").GetString()).IsEqualTo($"{7:x24}");
        var bare = await client.DocumentAsync(import, "users", ("n", "10"));
        await Assert.That(bare.TryGetProperty("_id", out _)).IsFalse();
    }

    [Test]
    public async Task An_export_imports_repeatedly_without_copying_the_previous_imports_audit_records()
    {
        await using var client = await ImportClient.StartAsync();
        var original = ImportClient.Data(await client.CreateDestinationAsync());
        var faker = new Faker();
        foreach (var collection in new[] { "items", "notes" })
        {
            using var made = await client.Client.PostAsJsonAsync(new Uri($"{original}/collections", UriKind.Relative), new { name = collection });
            await Assert.That(made.StatusCode).IsEqualTo(HttpStatusCode.Created);
            for (var i = 0; i < 25; i++)
            {
                using var put = await client.Client.PutAsJsonAsync(new Uri($"{original}/collections/{collection}/documents/by-key?id={Ulid.NewUlid()}", UriKind.Relative),
                    new { name = faker.Name.FullName(), n = i, nested = new { score = 0.5 + i, tags = faker.Lorem.Words(2) } });
                await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }
        }

        var export = await client.Client.GetByteArrayAsync(new Uri(original + "/export", UriKind.Relative));
        var import = await client.CreateDestinationAsync();
        await Assert.That((await client.CreateAsync(import, "ndjson", export)).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await client.UploadAsync(import, export)).Status).IsEqualTo(HttpStatusCode.OK);
        var ready = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready").Because(ready.GetRawText());
        await client.StartAsync(import, ready.GetProperty("preview").GetProperty("id").GetString());
        var done = await client.WaitAsync(import, "completed", "failed");
        await Assert.That(done.GetProperty("state").GetString()).IsEqualTo("completed").Because(done.GetRawText());

        var copy = await client.Client.GetStringAsync(new Uri(ImportClient.Data(import) + "/export", UriKind.Relative));
        // The copy also holds the import's own record collection, which sorts after the data.
        var lines = copy.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith("{\"collection\":\"terrascale-import\"", StringComparison.Ordinal));
        await Assert.That(string.Join('\n', lines) + "\n").IsEqualTo(Encoding.UTF8.GetString(export));

        // Import the untouched export, including both audit records. The new job owns fresh audit records.
        var next = await client.CreateDestinationAsync();
        var untouched = Encoding.UTF8.GetBytes(copy);
        await Assert.That((await client.CreateAsync(next, "ndjson", untouched)).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await client.UploadAsync(next, untouched)).Status).IsEqualTo(HttpStatusCode.OK);
        var nextReady = await client.WaitAsync(next, "ready", "rejected");
        await Assert.That(nextReady.GetProperty("state").GetString()).IsEqualTo("ready").Because(nextReady.GetRawText());
        var skipped = nextReady.GetProperty("preview").GetProperty("skipped").EnumerateArray().ToArray();
        await Assert.That(skipped.Length).IsEqualTo(2);
        await Assert.That(skipped.All(item => item.GetProperty("kind").GetString() == "import_audit_record")).IsTrue();
        await client.StartAsync(next, nextReady.GetProperty("preview").GetProperty("id").GetString());
        var nextDone = await client.WaitAsync(next, "completed", "failed");
        await Assert.That(nextDone.GetProperty("state").GetString()).IsEqualTo("completed").Because(nextDone.GetRawText());
        await Assert.That(nextDone.GetProperty("validation").GetProperty("passed").GetBoolean()).IsTrue();
        await Assert.That(nextDone.GetProperty("progress").GetProperty("rowsWritten").GetInt64()).IsEqualTo(50);
        await Assert.That(nextDone.GetProperty("id").GetString()).IsNotEqualTo(done.GetProperty("id").GetString());
        var nextCopy = await client.Client.GetStringAsync(new Uri(ImportClient.Data(next) + "/export", UriKind.Relative));
        var nextLines = nextCopy.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith("{\"collection\":\"terrascale-import\"", StringComparison.Ordinal));
        await Assert.That(string.Join('\n', nextLines) + "\n").IsEqualTo(Encoding.UTF8.GetString(export));
    }

    [Test]
    [Arguments("0000000000000000000000000J", "{\"v\":1,\"rev\":null,\"record\":\"{}\"}")]
    [Arguments("0000000000000000000000000S", "{\"v\":\"1\",\"rev\":null,\"record\":\"{}\"}")]
    [Arguments("00000000000000000000000001", "{\"customer\":\"keep this document\"}")]
    public async Task Customer_documents_in_the_audit_collection_are_never_silently_discarded(string id, string body)
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var source = Encoding.UTF8.GetBytes("{\"collection\":\"terrascale-import\",\"id\":\"" + id + "\",\"document\":" + body + "}\n");
        await client.CreateAsync(import, "ndjson", source);
        await client.UploadAsync(import, source);
        var rejected = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(rejected.GetProperty("state").GetString()).IsEqualTo("rejected");
        await Assert.That(rejected.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("collection_name_collision");
        await Assert.That(rejected.GetProperty("preview").GetProperty("skipped").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task A_line_without_a_collection_rejects_the_source()
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var source = "{\"collection\":\"items\",\"id\":\"a\",\"n\":1}\n{\"n\":2}\n"u8.ToArray();
        await client.CreateAsync(import, "ndjson", source);
        await client.UploadAsync(import, source);
        var rejected = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(rejected.GetProperty("state").GetString()).IsEqualTo("rejected");
        await Assert.That(rejected.GetProperty("error").GetProperty("message").GetString()).Contains("names no collection");
    }
}
