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

}
