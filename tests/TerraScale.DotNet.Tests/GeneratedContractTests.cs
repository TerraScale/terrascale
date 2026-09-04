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
        await Assert.That(TerraScaleContractRoutes.All).Count().IsEqualTo(35);

        var operation = TerraScaleContractRoutes.Find("execute_sql");
        await Assert.That(operation.Method).IsEqualTo("POST");
        await Assert.That(operation.PathTemplate).IsEqualTo("/api/v1/databases/{name}/sql");
        await Assert.That(operation.RequestType).IsEqualTo("SqlRequest");
        await Assert.That(operation.ResponseType).IsEqualTo("SqlResultResponse");
        await Assert.That(operation.RequestMediaTypes).Contains("application/json");
    }

    [Test]
    public async Task GeneratedJsonMetadataCoversPublishedModels()
    {
        var databaseType = TerraScaleContractJsonContext.Default.DatabaseRecord;
        var requestType = TerraScaleContractJsonContext.Default.CreateDatabaseRequest;

        await Assert.That(databaseType).IsNotNull();
        await Assert.That(requestType).IsNotNull();

        using var document = JsonDocument.Parse("{\"name\":\"demo\",\"placement\":[\"GRU\"]}");
        var request = JsonSerializer.Deserialize(document.RootElement.GetRawText(), requestType!);
        await Assert.That(request).IsNotNull();
    }

    [Test]
    public async Task GeneratedMessagePackMetadataPreservesTagsAndFields()
    {
        var protocolMajor = TerraScaleSyncV2Contract.ProtocolMajor;
        var protocolMinor = TerraScaleSyncV2Contract.ProtocolMinor;
        await Assert.That(protocolMajor).IsEqualTo(2);
        await Assert.That(protocolMinor).IsEqualTo(1);
        await Assert.That(TerraScaleSyncV2Contract.Messages).Count().IsEqualTo(11);

        var delta = TerraScaleSyncV2Contract.Messages.Single(message => message.Name == "delta");
        await Assert.That(delta.Tag).IsEqualTo(4);
        await Assert.That(delta.Fields).Contains("checkpoint");
        await Assert.That(delta.Direction).IsEqualTo("server_to_client");
    }
}
