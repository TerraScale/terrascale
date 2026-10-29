using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Hosting;

namespace TerraBase.Fast.Documents;

public sealed class CollectionSchemaMemoryTests
{
    private const string NameSchema = """{"properties":{"name":{"type":"string"}}}""";

    [Test]
    [Arguments(NameSchema)]
    [Arguments("""{"allOf":[{"properties":{"name":{"type":"string"}}}]}""")]
    [Arguments("""{"$defs":{"name":{"type":"string"}},"properties":{"name":{"$ref":"#/$defs/name"}}}""")]
    public Task Selective_property_schemas_accept_many_unrelated_values_under_a_small_memory_budget(string schema) =>
        ValidateSelectiveAsync(schema, $"[{string.Join(',', Enumerable.Repeat("0", 4000))}]");

    [Test]
    public Task Selective_property_schemas_accept_long_unrelated_strings_under_a_small_memory_budget()
    {
        // Repeated selective branches must not multiply the unrelated string's bytes into their result reservations.
        var schema = $$"""{"allOf":[{{string.Join(',', Enumerable.Repeat(NameSchema, 8))}}]}""";
        return ValidateSelectiveAsync(schema, $"\"{new string('x', 64 * 1024)}\"");
    }

    [Test]
    public async Task Disabled_uniqueness_accepts_large_duplicate_values_under_a_small_memory_budget()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var schemaPath = $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";
        var schema = "{\"properties\":{\"v\":{\"type\":\"array\",\"allOf\":[" + string.Join(',', Enumerable.Repeat("{\"uniqueItems\":false}", 64)) + "]}}}";
        (await api.SendRawAsync(HttpMethod.Put, schemaPath, schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var document = "{\"v\":[\"" + new string('x', 64 * 1024) + "\",0,0]}";
        var original = await api.SendRawAsync(HttpMethod.Put, api.Document(id), document, "application/json");
        original.Status.ShouldBe(HttpStatusCode.Created);
        var stored = await api.GetAsync(id);
        using var expected = JsonDocument.Parse(document);
        await Assert.That(JsonElement.DeepEquals(stored.Json.GetProperty("document"), expected.RootElement)).IsTrue();

        var rejected = await api.PutAsync(id, new { v = 42 });
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed"));
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(JsonElement.DeepEquals(unchanged.Json.GetProperty("document"), expected.RootElement)).IsTrue();
    }

    [Test]
    [Arguments("\"number\"")]
    [Arguments("[\"number\",\"string\"]")]
    [Arguments("[\"integer\",\"number\"]")]
    [Arguments("[\"number\",\"integer\"]")]
    public async Task Number_types_accept_long_numeric_lexical_forms_under_a_small_memory_budget(string type)
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var schemaPath = $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";
        var schema = "{\"properties\":{\"n\":{\"allOf\":[" + string.Join(',', Enumerable.Repeat("{\"type\":" + type + "}", 64)) + "]}}}";
        (await api.SendRawAsync(HttpMethod.Put, schemaPath, schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var number = "0." + new string('0', 512 * 1024);
        var original = await api.SendRawAsync(HttpMethod.Put, api.Document(id), "{\"n\":" + number + "}", "application/json");
        original.Status.ShouldBe(HttpStatusCode.Created);
        var stored = await api.GetAsync(id);
        await Assert.That(stored.Json.GetProperty("document").GetProperty("n").GetRawText()).IsEqualTo(number);

        var rejected = await api.PutAsync(id, new { n = true });
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed"));
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("n").GetRawText()).IsEqualTo(number);
        (await api.PutAsync(id, new { n = 2 })).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Nonnumeric_types_reject_long_numbers_with_validation_errors_under_a_small_memory_budget()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var schemaPath = $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";
        var schema = "{\"properties\":{\"n\":{\"allOf\":[" + string.Join(',', Enumerable.Repeat("{\"type\":\"string\"}", 64)) + "]}}}";
        (await api.SendRawAsync(HttpMethod.Put, schemaPath, schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { n = "small" });
        original.Status.ShouldBe(HttpStatusCode.Created);
        var rejected = await api.SendRawAsync(HttpMethod.Put, api.Document(id),
            "{\"n\":0." + new string('0', 512 * 1024) + "}", "application/json");
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed"));
        await Assert.That(rejected.Json.GetProperty("error").GetProperty("details").EnumerateArray().Any(detail =>
            detail.GetProperty("path").GetString() == "/n" && detail.GetProperty("keyword").GetString() == "type")).IsTrue();
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("n").GetString()).IsEqualTo("small");
        (await api.PutAsync(id, new { n = "replacement" })).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("{\"minimum\":1}")]
    [Arguments("{\"type\":\"integer\"}")]
    [Arguments("{\"type\":[\"integer\",\"null\"]}")]
    public async Task Numeric_error_text_is_admitted_before_evaluation_and_released_after_rejection(string constraint)
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var schemaPath = $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";
        var schema = "{\"properties\":{\"n\":{\"allOf\":[" + string.Join(',', Enumerable.Repeat(constraint, 64)) + "]}}}";
        (await api.SendRawAsync(HttpMethod.Put, schemaPath, schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { n = 1 });
        original.Status.ShouldBe(HttpStatusCode.Created);
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long evaluations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "terrabase.schema.keyword_evaluations" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            Interlocked.Add(ref evaluations, value);
        });
        listener.Start();
        var rejected = await api.SendRawAsync(HttpMethod.Put, api.Document(id),
            "{\"n\":0." + new string('0', 512 * 1024) + "}", "application/json");
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "storage_busy"));
        await Assert.That(Interlocked.Read(ref evaluations)).IsEqualTo(0);
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        (await api.PutAsync(id, new { n = 2 })).Status.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task ValidateSelectiveAsync(string schema, string unrelated)
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var schemaPath = $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";
        (await api.SendRawAsync(HttpMethod.Put, schemaPath, schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var document = $$"""{"name":"small","unrelated":{{unrelated}}}""";
        var original = await api.SendRawAsync(HttpMethod.Put, api.Document(id), document, "application/json");
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.Created);
        var stored = await api.GetAsync(id);
        await Assert.That(stored.Status).IsEqualTo(HttpStatusCode.OK);
        using var expected = JsonDocument.Parse(document);
        await Assert.That(JsonElement.DeepEquals(stored.Json.GetProperty("document"), expected.RootElement)).IsTrue();

        var rejected = await api.SendRawAsync(HttpMethod.Put, api.Document(id),
            $$"""{"name":42,"unrelated":{{unrelated}}}""", "application/json");
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed"));
        await Assert.That(rejected.Json.GetProperty("error").GetProperty("details").EnumerateArray().Any(detail =>
            detail.GetProperty("path").GetString() == "/name" && detail.GetProperty("keyword").GetString() == "type")).IsTrue();
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(JsonElement.DeepEquals(unchanged.Json.GetProperty("document"), expected.RootElement)).IsTrue();
    }
}
