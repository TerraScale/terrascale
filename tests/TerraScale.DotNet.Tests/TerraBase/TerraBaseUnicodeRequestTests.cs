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

    [Test]
    public async Task PropertyNamesNestedValuesMandatoryEscapesAndLiteralEscapeTextSurvive()
    {
        const string raw = """{"\u0080\u2028\u3000\u007f":{"nested":["\u0000\b\t\n\f\r\"\\", "\\u2028", "\uD83D\uDE00", 1.25e+10, -0, true, false, null]}}""";
        using var input = JsonDocument.Parse(raw);
        using var content = TerraBaseConnection.Json(input.RootElement, TerraBaseJsonContext.Wire.JsonElement);
        var bytes = await content.ReadAsByteArrayAsync();
        using var output = JsonDocument.Parse(bytes);
        await Assert.That(JsonElement.DeepEquals(input.RootElement, output.RootElement)).IsTrue();
        var wire = Encoding.UTF8.GetString(bytes);
        await Assert.That(wire.Contains("\\\\u2028", StringComparison.Ordinal)).IsTrue();
        await Assert.That(wire.Contains("😀", StringComparison.Ordinal)).IsTrue();
        await Assert.That(wire.Contains("\\u0000", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments("put", 1048575, false)]
    [Arguments("put", 1048577, true)]
    [Arguments("schema", 65535, false)]
    [Arguments("schema", 65537, true)]
    [Arguments("tx", 4194303, false)]
    [Arguments("tx", 4194305, true)]
    public async Task ContractAdmissionBoundariesUseActualCompactWireBytes(string operation, int size, bool tooLarge)
    {
        var parts = operation == "tx" ? 4 : 1;
        var emptyOps = Enumerable.Range(0, parts).Select(i => "{\"op\":\"put\",\"collection\":\"items\",\"id\":\"" + Id[..^1] + i + "\",\"document\":{}}");
        var overhead = operation == "tx" ? Encoding.UTF8.GetByteCount("{\"ops\":[" + string.Join(',', emptyOps) + "]}") - parts * 2 : 0;
        var payloadBytes = size - overhead;
        var documents = Enumerable.Range(0, parts).Select(i => Payload(payloadBytes / parts + (i < payloadBytes % parts ? 1 : 0))).ToArray();
        var limit = operation == "schema" ? 65536 : operation == "tx" ? 4194304 : 1048576;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            await Assert.That(bytes.Length).IsEqualTo(size);
            if (operation == "tx")
            {
                using var parsed = JsonDocument.Parse(bytes);
                foreach (var op in parsed.RootElement.GetProperty("ops").EnumerateArray())
                    await Assert.That(Encoding.UTF8.GetByteCount(op.GetProperty("document").GetRawText()) <= 1048576).IsTrue();
            }
            return bytes.Length > limit
                ? TerraBaseIntegrationFixture.Json("""{"error":{"code":"payload_too_large","message":"fixture admission"}}""", HttpStatusCode.RequestEntityTooLarge)
                : Reply(operation, parts);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        async Task Execute()
        {
            if (operation == "tx") await db.TransactAsync(documents.Select((document, i) => TerraBaseTransactionOperation.Put("items", Id[..^1] + i, document)).ToArray());
            else await Send(db, operation, documents[0]);
        }
        if (tooLarge)
        {
            var error = await Assert.ThrowsAsync<TerraBaseException>(Execute);
            await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
            await Assert.That(error.ErrorCode).IsEqualTo("payload_too_large");
        }
        else await Execute();
    }

    [Test]
    [Arguments("\"\\uD800\"")]
    [Arguments("\"\\uDC00\"")]
    [Arguments("{\"\\uD800\":0}")]
    public async Task MalformedUnicodeCannotReachTheTransport(string json)
    {
        using var input = JsonDocument.Parse(json);
        await Assert.That(() => TerraBaseConnection.Json(input.RootElement, TerraBaseJsonContext.Wire.JsonElement)).Throws<JsonException>();
    }

    [Test]
    public async Task ChunkBoundariesPreserveEscapesAndSurrogatePairs()
    {
        const string raw = """{"s":"\u2028\uD83D\uDE00\\u3000\"\\\u0000\u0008\/"}""";
        var bytes = Encoding.UTF8.GetBytes(raw);
        using var input = JsonDocument.Parse(bytes);
        for (var split = 1; split < bytes.Length; split++)
        {
            using var compact = new TerraBaseRequestJson();
            compact.Write(bytes.AsSpan(0, split));
            compact.Write(bytes.AsSpan(split));
            using var output = JsonDocument.Parse(compact.Finish());
            await Assert.That(JsonElement.DeepEquals(input.RootElement, output.RootElement)).IsTrue();
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
