using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseDocumentParityTests
{
    [Test]
    public async Task Collection_ULIDs_decimal_strings_and_lattice_patch_documents_survive_the_wire()
    {
        var calls = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Content is not null) calls.Add(await request.Content.ReadAsStringAsync(token));
            var body = path.EndsWith("/collections", StringComparison.Ordinal)
                ? """{"name":"items","id":"01JA0000000000000000000002","created_at":"1792627200000"}"""
                : path.EndsWith("/transactions", StringComparison.Ordinal)
                    ? """{"offset":"9007199254740993","durability":"remote_durable","results":[{"id":"01JA00000000000000000000D1","version":"tbv1-v","document":{"tags":["blue"]}},{"asserted":true,"count":"9007199254740994"}]}"""
                    : """{"id":"01JA00000000000000000000D1","version":"tbv1-v","offset":"9007199254740993","durability":"remote_durable","document":{"tags":["blue"]}}""";
            return TerraBaseIntegrationFixture.Json(body, path.EndsWith("/collections", StringComparison.Ordinal) ? HttpStatusCode.Created : HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var collection = await db.CreateCollectionAsync("items");
        using var scalar = JsonDocument.Parse("\"blue\"");
        var ops = new[] { TerraBasePatchOperation.Add("/tags", scalar.RootElement), TerraBasePatchOperation.Remove("/tags", scalar.RootElement) };
        var write = await db.PatchDocumentAsync("items", "01JA00000000000000000000D1", ops);
        var tx = await db.TransactAsync([TerraBaseTransactionOperation.Patch("items", "01JA00000000000000000000D1", ops), TerraBaseTransactionOperation.AssertCount("items", gte: 9007199254740994L)]);
        await Assert.That(collection.Id).IsEqualTo("01JA0000000000000000000002");
        await Assert.That(write.Offset).IsEqualTo(9007199254740993L);
        await Assert.That(write.Document!.Value.GetProperty("tags")[0].GetString()).IsEqualTo("blue");
        await Assert.That(tx.Results[0].Document!.Value.GetProperty("tags").GetArrayLength()).IsEqualTo(1);
        await Assert.That(tx.Results[1].Count).IsEqualTo(9007199254740994L);
        using var patch = JsonDocument.Parse(calls[1]);
        await Assert.That(patch.RootElement.GetProperty("ops")[0].GetProperty("op").GetString()).IsEqualTo("add");
        await Assert.That(patch.RootElement.GetProperty("ops")[1].GetProperty("op").GetString()).IsEqualTo("remove");
        using var transaction = JsonDocument.Parse(calls[2]);
        await Assert.That(transaction.RootElement.GetProperty("ops")[1].GetProperty("gte").GetInt64()).IsEqualTo(9007199254740994L);
    }

    [Test]
    public async Task Ambiguous_write_identity_and_schema_errors_remain_visible_without_retry()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json(calls == 1
                ? """{"error":{"code":"write_outcome_unknown","message":"confirmation unknown","region":"gru","machine":"test-machine"}}"""
                : """{"error":{"code":"schema_validation_failed","message":"schema refused","op":1,"details":[{"path":"/title","keyword":"type"}]}}""",
                calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var json = JsonDocument.Parse("{}");
        var unknown = await Assert.ThrowsAsync<TerraBaseUnavailableException>(() => db.PutDocumentAsync("items", "01JA00000000000000000000D1", json.RootElement));
        await Assert.That(unknown!.Machine).IsEqualTo("test-machine");
        await Assert.That(unknown.OutcomeRegion).IsEqualTo("gru");
        await Assert.That(unknown.RetryAfter).IsNull();
        var invalid = await Assert.ThrowsAsync<TerraBaseValidationException>(() => db.TransactAsync([TerraBaseTransactionOperation.Put("items", "01JA00000000000000000000D1", json.RootElement)]));
        await Assert.That(invalid!.OperationIndex).IsEqualTo(1);
        await Assert.That(invalid.Details.Single().Path).IsEqualTo("/title");
        await Assert.That(invalid.Details.Single().Keyword).IsEqualTo("type");
        await Assert.That(calls).IsEqualTo(2);
    }
}
