using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseUnicodeRequestTests
{
    private const string Id = "01JA00000000000000000000D1";

    [Test]
    [Arguments('\u2028')]
    [Arguments('\u3000')]
    [Arguments('\u007f')]
    [Arguments('\u0080')]
    [Arguments('界')]
    public async Task EveryWriteFamilyPreservesCompactUnicodeAndEscapedReadPayloads(char character)
    {
        foreach (var operation in new[] { "create", "put", "merge", "ops", "schema", "tx" })
        {
            var count = operation == "schema" ? 9000 : 200000;
            var raw = "{\"description\":\"" + new string(character, count) + "\"}";
            using var input = JsonDocument.Parse(raw);
            // Default producer-style escaping must not become the next request's size.
            using var read = JsonDocument.Parse(JsonSerializer.Serialize(input.RootElement));
            var calls = 0;
            using var handler = new TerraBaseTestHandler(async (request, token) =>
            {
                calls++;
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                using var sent = JsonDocument.Parse(bytes);
                var document = operation switch
                {
                    "tx" => sent.RootElement.GetProperty("ops")[0].GetProperty("document"),
                    "ops" => sent.RootElement.GetProperty("ops")[0].GetProperty("value"),
                    _ => sent.RootElement,
                };
                await Assert.That(JsonElement.DeepEquals(input.RootElement, document)).IsTrue();
                await Assert.That(bytes.Length <= Encoding.UTF8.GetByteCount(raw) + 200).IsTrue();
                await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
                await Assert.That(request.Content.Headers.ContentType!.MediaType).IsEqualTo(operation switch
                {
                    "merge" => "application/merge-patch+json",
                    "ops" => "application/vnd.terrabase.ops+json",
                    _ => "application/json",
                });
                return Reply(operation);
            });
            using var http = new HttpClient(handler);
            var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
            await Send(db, operation, read.RootElement);
            await Assert.That(calls).IsEqualTo(1);
        }
    }

    private static JsonElement Payload(int size)
    {
        var stringBytes = size - 18; // {"description":""}
        using var input = JsonDocument.Parse("{\"description\":\"" + new string('\u0080', stringBytes / 2) + new string('a', stringBytes % 2) + "\"}");
        return input.RootElement.Clone();
    }

    private static async Task Send(TerraBaseDatabase db, string operation, JsonElement document)
    {
        switch (operation)
        {
            case "create": await db.CreateDocumentAsync("items", document); break;
            case "put": await db.PutDocumentAsync("items", Id, document); break;
            case "merge": await db.PatchDocumentAsync("items", Id, document); break;
            case "ops": await db.PatchDocumentAsync("items", Id, [TerraBasePatchOperation.Set("value", document)]); break;
            case "schema": await db.PutDeclaredSchemaAsync("items", document); break;
            case "tx": await db.TransactAsync([TerraBaseTransactionOperation.Put("items", Id, document)]); break;
        }
    }

    private static HttpResponseMessage Reply(string operation, int parts = 1) => TerraBaseIntegrationFixture.Json(operation switch
    {
        "schema" => """{"schema":{},"dialect":"https://json-schema.org/draft/2020-12/schema"}""",
        "tx" => "{\"offset\":\"1\",\"durability\":\"local_durable\",\"results\":[" + string.Join(',', Enumerable.Repeat("{\"id\":\"" + Id + "\",\"version\":\"opaque\"}", parts)) + "]}",
        _ => """{"id":"01JA00000000000000000000D1","version":"opaque","offset":"1","durability":"local_durable"}""",
    });
}
