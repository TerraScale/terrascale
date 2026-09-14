using System.Text.Json;
using TerraScale.DotNet.Generated;

namespace TerraScale.DotNet.Tests;

public sealed class GeneratedContractTests
{
    [Test]
    public async Task GeneratedPinAndRouteCatalogAreV1()
    {
        await Assert.That(TerraScaleContractPin.Version).IsEqualTo("v1");
        await Assert.That(TerraScaleContractPin.ManifestSha256.Length).IsEqualTo(64);

        var operation = TerraScaleContractRoutes.Find("execute_sql");
        await Assert.That(operation.Method).IsEqualTo("POST");
        await Assert.That(operation.PathTemplate).IsEqualTo("/api/v1/databases/{name}/sql");
        await Assert.That(operation.RequestType).IsEqualTo("SqlRequest");
        await Assert.That(operation.ResponseType).IsEqualTo("JsonElement");
        await Assert.That(operation.RequestMediaTypes).Contains("application/json");
        await Assert.That(TerraScaleContractJsonContext.Default.SqlResponse).IsNotNull();
        await Assert.That(TerraScaleContractJsonContext.Default.SqlExplainResponse).IsNotNull();
    }

    [Test]
    public async Task GeneratedJsonMetadataCoversPublishedModels()
    {
        var databaseType = TerraScaleContractJsonContext.Default.DatabaseRecord;
        var requestType = TerraScaleContractJsonContext.Default.CreateDatabaseRequest;

        await Assert.That(databaseType).IsNotNull();
        await Assert.That(requestType).IsNotNull();

        using var document = JsonDocument.Parse("{\"name\":\"demo\",\"placement\":[\"GRU\"],\"operation_id\":\"op-1\",\"residency\":null}");
        var request = JsonSerializer.Deserialize(document.RootElement.GetRawText(), requestType!);
        await Assert.That(request).IsNotNull();
    }

    [Test]
    public async Task GeneratedWebhookAndIncrementModelsRoundTripWireNames()
    {
        var webhook = JsonSerializer.Deserialize(
            "{\"id\":\"hook-1\",\"endpoint\":\"https://receiver.test/hook\",\"created_at\":7,\"deleted\":false,\"secret\":\"key\"}",
            TerraScaleContractJsonContext.Default.WebhookSecretResponse);
        await Assert.That(webhook).IsNotNull();
        await Assert.That(webhook!.Secret).IsEqualTo("key");

        var increment = new Increment(
            new Dictionary<string, double> { ["count"] = 2 },
            "orders",
            [],
            null);
        var json = JsonSerializer.Serialize(increment, TerraScaleContractJsonContext.Default.Increment);
        using var document = JsonDocument.Parse(json);
        await Assert.That(document.RootElement.GetProperty("increment").GetProperty("count").GetDouble()).IsEqualTo(2d);
        await Assert.That(document.RootElement.TryGetProperty("IncrementValue", out _)).IsFalse();
    }

    [Test]
    public async Task GeneratedMessagePackMetadataPreservesTagsAndFields()
    {
        var protocolMajor = TerraScaleSyncV1Contract.ProtocolMajor;
        var protocolMinor = TerraScaleSyncV1Contract.ProtocolMinor;
        await Assert.That(protocolMajor).IsEqualTo(1);
        await Assert.That(protocolMinor).IsEqualTo(0);
        await Assert.That(TerraScaleSyncV1Contract.Messages).Count().IsEqualTo(11);

        var delta = TerraScaleSyncV1Contract.Messages.Single(message => message.Name == "delta");
        await Assert.That(delta.Tag).IsEqualTo(4);
        await Assert.That(delta.Fields).Contains("checkpoint");
        await Assert.That(delta.Direction).IsEqualTo("server_to_client");
    }

    [Test]
    public async Task GeneratedBranchModelsRoundTripPublishedWireNames()
    {
        var request = new RestoreRequest(7, "branch-a", "restore-op-1");
        var json = JsonSerializer.Serialize(request, TerraScaleContractJsonContext.Default.RestoreRequest);
        await Assert.That(json).IsEqualTo("{\"at\":7,\"name\":\"branch-a\",\"operation_id\":\"restore-op-1\"}");

        var response = JsonSerializer.Deserialize(
            "{\"name\":\"branch-a\",\"placement\":\"GRU\",\"residency\":\"br-only\",\"deleted\":false,\"created_at\":7,\"parent\":\"main\"}",
            TerraScaleContractJsonContext.Default.DatabaseRecord);
        await Assert.That(response).IsNotNull();
        await Assert.That(response!.Parent).IsEqualTo("main");
    }

    [Test]
    public async Task GeneratedSchemaAndQueryModelsKeepTypedJsonBranches()
    {
        var schema = JsonSerializer.Deserialize(
            "{\"name\":\"orders\",\"fields\":[{\"name\":\"total\",\"type\":\"float\"}],\"mergePolicy\":\"hlc-tiebreak\",\"fts\":{\"fields\":[\"title\"]},\"ttl\":{\"field\":\"expires_at\"}}",
            TerraScaleContractJsonContext.Default.CollectionSchema);

        await Assert.That(schema).IsNotNull();
        await Assert.That(schema!.Fields![0].Type).IsEqualTo(FieldType.Float);
        await Assert.That(schema.MergePolicy).IsEqualTo(MergePolicy.HlcTiebreak);
        await Assert.That(schema.Fts!.Fields).Contains("title");
        await Assert.That(schema.Ttl!.Field).IsEqualTo("expires_at");

        var query = JsonSerializer.Deserialize(
            "{\"model\":\"orders\",\"predicates\":[{\"field\":\"status\",\"operator\":\"eq\",\"value\":\"open\"}],\"offset\":0,\"limit\":25,\"sortBy\":{\"field\":\"total\",\"direction\":\"desc\"}}",
            TerraScaleContractJsonContext.Default.PageQuery);

        await Assert.That(query).IsNotNull();
        await Assert.That(query!.SortBy!.Direction).IsEqualTo(PageSortDirection.Desc);
        await Assert.That(query.Predicates[0].Operator).IsEqualTo(PredicateOperatorSchema.Eq);

        var importJson = JsonSerializer.Serialize(
            new CreateImport(TransferFormat.MongoJson, "import-1"),
            TerraScaleContractJsonContext.Default.CreateImport);
        await Assert.That(importJson).IsEqualTo("{\"format\":\"mongo_json\",\"idempotency_key\":\"import-1\"}");
    }
}
