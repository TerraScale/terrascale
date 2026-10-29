using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Schema wire boundaries from TerraBase 1c2a7e082829bcebf816691537001f6aba168d9a; no simulated server.</summary>
public sealed class TerraBaseSchemaTests
{
    [Test]
    public async Task SingleAndCompoundDeclarationsUseTheProducerRequestShape()
    {
        var schema = new TerraBaseUniqueSchemaRequest(
            [new("email", Unique: true, Normalization: "trim_lowercase")],
            [new("tenant_handle", ["tenant", "profile.handle"], Unique: true, Normalization: "lowercase")]);
        var json = JsonSerializer.Serialize(schema, TerraBaseJsonContext.Wire.TerraBaseUniqueSchemaRequest);
        await Assert.That(json).IsEqualTo(
            """{"fields":[{"name":"email","unique":true,"normalization":"trim_lowercase"}],"indexes":[{"name":"tenant_handle","fields":["tenant","profile.handle"],"unique":true,"normalization":"lowercase"}]}""");
        await Assert.That(JsonSerializer.Serialize(new TerraBaseUniqueSchemaRequest(), TerraBaseJsonContext.Wire.TerraBaseUniqueSchemaRequest))
            .IsEqualTo("{}");
    }

    [Test]
    public async Task DeclarationAndInferenceHaveDifferentResponseEnvelopes()
    {
        const string constraint = """{"name":"tenant_handle","fields":["tenant","profile.handle"],"normalization":"lowercase"}""";
        var declared = TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes("{\"constraints\":[" + constraint + "]}"), TerraBaseJsonContext.Wire.TerraBaseUniqueSchema, HttpStatusCode.OK);
        await Assert.That(declared.Constraints[0].Fields).IsEquivalentTo(new[] { "tenant", "profile.handle" });
        await Assert.That(declared.Constraints[0].Normalization).IsEqualTo("lowercase");

        var inferred = TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes("""{"collection":"people","documents":2,"sampledDocuments":2,"fields":[{"path":"tenant","present":2,"types":{"string":2}}],"uniqueConstraints":[""" + constraint + "]}"),
            TerraBaseJsonContext.Wire.TerraBaseCollectionSchema, HttpStatusCode.OK);
        await Assert.That(inferred.Collection).IsEqualTo("people");
        await Assert.That(inferred.Documents).IsEqualTo(2);
        await Assert.That(inferred.SampledDocuments).IsEqualTo(2);
        await Assert.That(inferred.Fields[0].Types["string"]).IsEqualTo(2);
        await Assert.That(inferred.UniqueConstraints![0].Name).IsEqualTo("tenant_handle");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"constraints\":null}")]
    [Arguments("{\"constraints\":[null]}")]
    [Arguments("{\"constraints\":[{\"name\":\"email\",\"fields\":null,\"normalization\":\"none\"}]}")]
    [Arguments("{\"constraints\":[{\"name\":\"email\",\"fields\":[null],\"normalization\":\"none\"}]}")]
    [Arguments("{\"constraints\":[{\"name\":\"email\",\"fields\":[\"email\"]}]}")]
    public async Task MalformedDeclarationsAreInvalidResponses(string json)
    {
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => Task.FromResult(TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes(json), TerraBaseJsonContext.Wire.TerraBaseUniqueSchema, HttpStatusCode.OK)));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments("{\"collection\":\"people\",\"documents\":0,\"sampledDocuments\":0,\"fields\":[null]}")]
    [Arguments("{\"collection\":\"people\",\"documents\":0,\"sampledDocuments\":0,\"fields\":[],\"uniqueConstraints\":[null]}")]
    public async Task InferenceRejectsNullListEntries(string json)
    {
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => Task.FromResult(TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes(json), TerraBaseJsonContext.Wire.TerraBaseCollectionSchema, HttpStatusCode.OK)));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task UniqueViolationPreservesTheFailingTransactionIndex()
    {
        using var content = new StringContent("""{"error":{"code":"unique_violation","message":"a unique field or compound key is already taken","op":1}}""");
        var error = await TerraBaseException.FromResponseAsync(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = content }, CancellationToken.None);
        await Assert.That(error.GetType()).IsEqualTo(typeof(TerraBaseConflictException));
        await Assert.That(error.ErrorCode).IsEqualTo("unique_violation");
        await Assert.That(error.OperationIndex).IsEqualTo(1);
    }
}
