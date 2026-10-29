using System.Net;
using System.Text.Json;
using Bogus;

namespace TerraBase.Fast.Documents;

public sealed class CollectionSchemaAdmissionTests
{
    [Test]
    [Arguments("""{"type":"object","type":"array"}""")]
    [Arguments("""{"type":"object","\u0074ype":"array"}""")]
    public async Task Duplicate_decoded_schema_members_are_rejected_without_replacing_the_previous_schema(string invalid)
    {
        await using var api = await DocumentApi.StartAsync();
        const string Previous = """{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""";
        await AttachAsync(api, Previous);
        await RejectedAttachmentAsync(api, invalid, Previous);
        await FailureAsync(await api.PutAsync(DocumentApi.NewId(), new { other = true }), "", "required");
        (await api.PutAsync(DocumentApi.NewId(), new { name = "preserved" })).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Test]
    [Arguments("""{"$ref":null}""")]
    [Arguments("""{"properties":{"name":{"$ref":null}}}""")]
    [Arguments("""{"$ref":false}""")]
    [Arguments("""{"$ref":42}""")]
    [Arguments("""{"$ref":{}}""")]
    [Arguments("""{"$ref":[]}""")]
    [Arguments("""{"$ref":"#/examples/0","examples":[{"$ref":null}]}""")]
    public async Task Non_string_references_are_rejected_without_changing_the_schema_or_documents(string invalid)
    {
        await using var api = await DocumentApi.StartAsync();
        const string Previous = """{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""";
        await AttachAsync(api, Previous);
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { name = "original" })).Status.ShouldBe(HttpStatusCode.Created);
        var before = await api.GetAsync(id);

        await RejectedAttachmentAsync(api, invalid, Previous);
        await FailureAsync(await api.PutAsync(id, new { name = 42 }), "/name", "type");
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(before.Version);
        await Assert.That(JsonElement.DeepEquals(unchanged.Json.GetProperty("document"), before.Json.GetProperty("document"))).IsTrue();

        (await api.PutAsync(id, new { name = "after rejection" })).Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("after rejection");
    }

    [Test]
    public async Task Local_pointer_references_enforce_constraints_and_invalid_references_preserve_the_schema()
    {
        await using var api = await DocumentApi.StartAsync();
        const string Schema = """
            {
              "$defs": {
                "positive/value~": {"type":"integer","minimum":1},
                "alias": {"$ref":"#/$defs/positive~1value~0"}
              },
              "type":"object",
              "required":["name","counts"],
              "properties": {
                "name":{"type":"string","minLength":2},
                "counts":{"type":"array","items":{"$ref":"#/$defs/alias"}}
              },
              "additionalProperties":false
            }
            """;
        await AttachAsync(api, Schema);
        var id = DocumentApi.NewId();
        var name = new Faker().Random.AlphaNumeric(12);
        int[] validCounts = [1, 2];
        int[] zeroCounts = [0];
        string[] wrongCounts = ["wrong"];
        int[] oneCount = [1];
        (await api.PutAsync(id, new { name, counts = validCounts })).Status.ShouldBe(HttpStatusCode.Created);
        await FailureAsync(await api.PutAsync(id, new { name, counts = zeroCounts }), "/counts/0", "minimum");
        await FailureAsync(await api.PutAsync(id, new { name, counts = wrongCounts }), "/counts/0", "type");
        await FailureAsync(await api.PutAsync(id, new { name = "x", counts = oneCount }), "/name", "minLength");
        // The false subschema fails at the extra property's location, rather than the root object.
        await FailureAsync(await api.PutAsync(id, new { name, counts = oneCount, extra = true }), "/extra", "schema");
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("counts")[0].GetInt32()).IsEqualTo(1);

        string[] invalidReferences =
        [
            """{"$ref":"#/$defs/missing"}""",
            """{"$ref":"#/examples/0","examples":[{"multipleOf":0}]}""",
            """{"$ref":"#named-anchor"}""",
            """{"$ref":"https://example.invalid/schema"}""",
            """{"$ref":"other.json#/$defs/value"}""",
            """{"$ref":"#"}""",
            """{"$defs":{"a":{"$ref":"#/$defs/b"},"b":{"$ref":"#/$defs/a"}},"$ref":"#/$defs/a"}""",
        ];
        foreach (var invalid in invalidReferences)
        {
            await RejectedAttachmentAsync(api, invalid, Schema);
        }

        await FailureAsync(await api.PutAsync(DocumentApi.NewId(), new { name, counts = new[] { -1 } }), "/counts/0", "minimum");
    }

    [Test]
    public async Task Dialect_and_regex_restrictions_apply_to_schemas_while_keyword_looking_data_and_format_are_allowed()
    {
        await using var api = await DocumentApi.StartAsync();
        const string Data = """
            {"nested":{"$id":"data","$anchor":"data","$dynamicRef":"data","$dynamicAnchor":"data",
            "$recursiveRef":"data","$recursiveAnchor":true,"$vocabulary":{},"$schema":"data",
            "$ref":"https://example.invalid/data","pattern":"[","patternProperties":{"[":false}}}
            """;
        var schema = $$"""
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "type":"object",
              "required":["payload","choice","email","pattern"],
              "properties": {
                "payload":{"const":{{Data}}},
                "choice":{"enum":[{{Data}}]},
                "email":{"type":"string","format":"email"},
                "pattern":{"type":"string"}
              },
              "examples":[{{Data}}]
            }
            """;
        await AttachAsync(api, schema);
        var accepted = await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            $$"""{"payload":{{Data}},"choice":{{Data}},"email":"not an email","pattern":"["}""", "application/json");
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.Created);
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            $$"""{"payload":{},"choice":{{Data}},"email":"not an email","pattern":"["}""", "application/json"), "/payload", "const");

        string[] invalidSchemas =
        [
            """{"$schema":"http://json-schema.org/draft-07/schema#"}""",
            """{"$schema":"https://example.invalid/schema"}""",
            """{"$id":"https://example.invalid/resource"}""",
            """{"$anchor":"named"}""",
            """{"$dynamicRef":"#named"}""",
            """{"$dynamicAnchor":"named"}""",
            """{"$recursiveRef":"#"}""",
            """{"$recursiveAnchor":true}""",
            """{"$vocabulary":{}}""",
            """{"pattern":"a"}""",
            """{"patternProperties":{"a":true}}""",
            """{"properties":{"value":{"pattern":"a"}}}""",
            """{"$defs":{"unused":{"$id":"https://example.invalid/resource"}}}""",
            """{"items":{"$anchor":"nested"}}""",
            """{"allOf":[{"patternProperties":{"a":true}}]}""",
        ];
        foreach (var invalid in invalidSchemas)
        {
            await RejectedAttachmentAsync(api, invalid, schema);
        }
    }

    [Test]
    public async Task Admission_bounds_expanded_references_json_depth_and_total_values()
    {
        await using var api = await DocumentApi.StartAsync();
        // Root + definition + 63 reference objects + 63 expanded targets = 128 schema nodes.
        var properties = string.Join(',', Enumerable.Range(0, 63).Select(index => $$""" "p{{index}}":{"$ref":"#/$defs/value"} """));
        var atGraphLimit = $$"""{"$defs":{"value":true},"properties":{ {{properties}} } }""";
        await AttachAsync(api, atGraphLimit);
        (await api.PutAsync(DocumentApi.NewId(), new { p0 = 1, p62 = false })).Status.ShouldBe(HttpStatusCode.Created);
        var overGraphLimit = $$"""{"$defs":{"value":true},"properties":{ {{properties}},"p63":{"$ref":"#/$defs/value"} } }""";
        await RejectedAttachmentAsync(api, overGraphLimit, atGraphLimit);

        // Annotation data consumes JSON admission limits, without expanding the schema graph.
        var atDepthLimit = $$"""{"examples":[{{string.Concat(Enumerable.Repeat("{\"a\":", 13))}}0{{new string('}', 13)}}]}""";
        await AttachAsync(api, atDepthLimit);
        var overDepthLimit = $$"""{"examples":[{{string.Concat(Enumerable.Repeat("{\"a\":", 14))}}0{{new string('}', 14)}}]}""";
        await RejectedAttachmentAsync(api, overDepthLimit, atDepthLimit);

        // One root object + one examples array + 4090 numbers = 4092 schema values; the metadata envelope consumes four more.
        var values = string.Join(',', Enumerable.Repeat("0", 4090));
        var atValueLimit = $$"""{"examples":[{{values}}]}""";
        await AttachAsync(api, atValueLimit);
        await RejectedAttachmentAsync(api, $$"""{"examples":[{{values}},0]}""", atValueLimit);
    }

    [Test]
    public async Task Numeric_comparison_and_multiple_of_accept_document_extremes_without_rounding_or_server_errors()
    {
        await using var api = await DocumentApi.StartAsync();
        const string ExtremeSchema = """
            {"type":"object","required":["n"],"properties":{"n":{"type":"number",
            "minimum":-1e307,"maximum":1e307,"multipleOf":1e-307}}}
            """;
        await AttachAsync(api, ExtremeSchema);
        var digits = new string('3', 128);
        string[] validNumbers = ["0", "1e307", "-1e307", "1e-307", "-1e-307", digits, "-" + digits, "1." + new string('7', 127)];
        foreach (var number in validNumbers)
        {
            var id = DocumentApi.NewId();
            var written = await api.SendRawAsync(HttpMethod.Put, api.Document(id), $$"""{"n":{{number}}}""", "application/json");
            await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
            await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("n").GetRawText()).IsEqualTo(number);
        }

        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), """{"n":2e307}""", "application/json"), "/n", "maximum");
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), """{"n":-2e307}""", "application/json"), "/n", "minimum");
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), """{"n":1.5e-307}""", "application/json"), "/n", "multipleOf");

        var prefix = new string('3', 127);
        var exactSchema = $$"""{"properties":{"n":{"type":"number","minimum":{{digits}},"maximum":{{prefix}}5,"multipleOf":3} } }""";
        await AttachAsync(api, exactSchema);
        (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), $$"""{"n":{{digits}}}""", "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), $$"""{"n":{{prefix}}2}""", "application/json"), "/n", "minimum");
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), $$"""{"n":{{prefix}}6}""", "application/json"), "/n", "maximum");
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), $$"""{"n":{{prefix}}4}""", "application/json"), "/n", "multipleOf");
    }

    [Test]
    public async Task Integer_equality_and_uniqueness_preserve_exact_json_numbers()
    {
        await using var api = await DocumentApi.StartAsync();
        await AttachAsync(api, """{"properties":{"n":{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"integer"}}}""");
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            """{"n":9007199254740992.5}""", "application/json"), "/n", "type");
        (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            """{"n":1e100}""", "application/json")).Status.ShouldBe(HttpStatusCode.Created);

        await AttachAsync(api, """{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"n":{"const":1e-40},"choice":{"enum":[1e100]}}}""");
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            """{"n":2e-40}""", "application/json"), "/n", "const");
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            """{"choice":2e100}""", "application/json"), "/choice", "enum");
        (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            """{"n":1e-40,"choice":1e100}""", "application/json")).Status.ShouldBe(HttpStatusCode.Created);

        await AttachAsync(api, """{"properties":{"a":{"uniqueItems":true}}}""");
        (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            """{"a":[1e100,2e100,1e-40,2e-40]}""", "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            """{"a":[{"n":1},{"n":1.0}]}""", "application/json"), "/a", "uniqueItems");
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            """{"a":[1e100,10e99]}""", "application/json"), "/a", "uniqueItems");
    }

    [Test]
    public async Task Fractional_integer_limits_are_rejected_without_rounding_even_in_nested_and_referenced_schemas()
    {
        await using var api = await DocumentApi.StartAsync();
        const string Previous = """{"properties":{"n":{"type":"integer","minimum":1}}}""";
        await AttachAsync(api, Previous);
        string[] keywords =
        [
            "minLength", "maxLength", "minItems", "maxItems",
            "minContains", "maxContains", "minProperties", "maxProperties",
        ];
        string[] fractionalLimits = ["1.0000000000000001", "1e-40"];
        foreach (var keyword in keywords)
        {
            foreach (var limit in fractionalLimits)
            {
                var invalid = $$"""{"{{keyword}}":{{limit}}}""";
                await RejectedAttachmentAsync(api, invalid, Previous);
                await RejectedAttachmentAsync(api, $$"""{"properties":{"value":{{invalid}} } }""", Previous);
                // Annotation payloads become schemas when a local reference targets them.
                await RejectedAttachmentAsync(api, $$"""{"$ref":"#/examples/0","examples":[{{invalid}}]}""", Previous);
            }
        }

        await FailureAsync(await api.PutAsync(DocumentApi.NewId(), new { n = 0 }), "/n", "minimum");
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Test]
    public async Task String_length_counts_unicode_code_points_including_combining_marks_and_astral_characters()
    {
        await using var api = await DocumentApi.StartAsync();
        const string TwoCodePoints = """{"properties":{"text":{"type":"string","minLength":2,"maxLength":2}}}""";
        const string OneCodePoint = """{"properties":{"text":{"type":"string","minLength":1,"maxLength":1}}}""";
        const string CombiningMark = """{"text":"e\u0301"}""";
        const string AstralEmoji = """{"text":"\ud83d\ude00"}""";
        await AttachAsync(api, TwoCodePoints);
        (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), CombiningMark, "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), AstralEmoji, "application/json"), "/text", "minLength");

        await AttachAsync(api, OneCodePoint);
        (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), AstralEmoji, "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        await FailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), CombiningMark, "application/json"), "/text", "maxLength");
    }

    [Test]
    [Arguments("true")]
    [Arguments("{\"type\":\"object\",\"minProperties\":1}")]
    [Arguments("{\"allOf\":[{\"type\":\"object\"},{\"minProperties\":1}]}")]
    [Arguments("{\"$defs\":{\"root\":{\"type\":\"object\"}},\"$ref\":\"#/$defs/root\"}")]
    [Arguments("""{"$defs":{"unused":{"items":true}}}""")]
    [Arguments("""{"allOf":[{"$defs":{"unused":{"items":true}},"type":"object"}]}""")]
    [Arguments("""{"$defs":{"root":{"$defs":{"unused":{"items":true}},"type":"object"}},"$ref":"#/$defs/root"}""")]
    public async Task Root_only_schemas_leave_large_documents_writable_under_a_small_memory_budget(string schema)
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        await AttachAsync(api, schema);
        var id = DocumentApi.NewId();
        var document = $$"""{"a":[{{string.Join(',', Enumerable.Repeat("0", 4000))}}]}""";
        var accepted = await api.SendRawAsync(HttpMethod.Put, api.Document(id), document, "application/json");
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.Created);
        var stored = await api.GetAsync(id);
        await Assert.That(stored.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stored.Json.GetProperty("document").GetProperty("a").GetArrayLength()).IsEqualTo(4000);
    }

    [Test]
    public async Task Schema_expansion_over_the_memory_budget_rejects_writes_until_the_schema_is_detached()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        // Root + array property + items schema + 120 allOf schemas = 123 admitted nodes.
        var emptySchemas = string.Join(',', Enumerable.Repeat("{}", 120));
        var schema = $$"""{"properties":{"a":{"items":{"allOf":[{{emptySchemas}}]} } } }""";
        await AttachAsync(api, schema);
        var id = DocumentApi.NewId();
        var document = $$"""{"a":[{{string.Join(',', Enumerable.Repeat("0", 4000))}}]}""";
        var rejected = await api.SendRawAsync(HttpMethod.Put, api.Document(id), document, "application/json");
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "storage_busy"));
        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);

        (await api.SendAsync(HttpMethod.Delete, SchemaPath(api))).Status.ShouldBe(HttpStatusCode.OK);
        (await api.SendRawAsync(HttpMethod.Put, api.Document(id), document, "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        var stored = (await api.GetAsync(id)).Json.GetProperty("document").GetProperty("a");
        await Assert.That(stored.GetArrayLength()).IsEqualTo(4000);
        await Assert.That(stored.EnumerateArray().All(item => item.GetInt32() == 0)).IsTrue();
    }

    private static string SchemaPath(DocumentApi api) => $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";

    private static async Task AttachAsync(DocumentApi api, string schema)
    {
        var response = await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), schema, "application/json");
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        using var expected = JsonDocument.Parse(schema);
        await Assert.That(JsonElement.DeepEquals(response.Json.GetProperty("schema"), expected.RootElement)).IsTrue();
        await Assert.That(response.Json.GetProperty("dialect").GetString()).IsEqualTo("https://json-schema.org/draft/2020-12/schema");
    }

    private static async Task RejectedAttachmentAsync(DocumentApi api, string invalid, string previous)
    {
        var rejected = await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), invalid, "application/json");
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_schema"));
        var read = await api.SendAsync(HttpMethod.Get, SchemaPath(api));
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        using var expected = JsonDocument.Parse(previous);
        await Assert.That(JsonElement.DeepEquals(read.Json.GetProperty("schema"), expected.RootElement)).IsTrue();
    }

    private static async Task FailureAsync(ApiResponse response, string path, string keyword)
    {
        var diagnostic = $"expected {keyword} at {path}; HTTP {(int)response.Status}: {response.Json.GetRawText()}";
        await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed")).Because(diagnostic);
        var details = response.Json.GetProperty("error").GetProperty("details");
        await Assert.That(details.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(details.EnumerateArray().Any(detail => detail.GetProperty("path").GetString() == path
            && detail.GetProperty("keyword").GetString() == keyword)).IsTrue().Because(diagnostic);
    }
}
