using TerraBase.Testing;
using System.Net;

namespace TerraBase.Slow.Documents;

public sealed class CollectionSchemaUniquenessTests
{
    [Test]
    public async Task Repeated_uniqueness_constraints_accept_large_distinct_arrays_and_reject_duplicate_replacements()
    {
        // Admission conservatively reserves every schema against every document value.
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "8589934592");
        const string Schema = """{"properties":{"a":{"allOf":[{"uniqueItems":true},{"uniqueItems":true},{"uniqueItems":true},{"uniqueItems":true}]}}}""";
        (await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), Schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var distinct = Enumerable.Range(0, 4000).ToArray();
        var original = await api.PutAsync(id, new { a = distinct });
        original.Status.ShouldBe(HttpStatusCode.Created);
        distinct[^1] = distinct[0];
        await RejectedAsync(await api.PutAsync(id, new { a = distinct }));
        var stored = await api.GetAsync(id);
        await Assert.That(stored.Version).IsEqualTo(original.Version);
        var values = stored.Json.GetProperty("document").GetProperty("a");
        await Assert.That(values.GetArrayLength()).IsEqualTo(4000);
        await Assert.That(values[3999].GetInt32()).IsEqualTo(3999);
    }

    [Test]
    public async Task Uniqueness_uses_exact_numbers_decoded_strings_unordered_objects_and_ordered_arrays()
    {
        await using var api = await DocumentApi.StartAsync();
        const string Schema = """{"properties":{"a":{"uniqueItems":true}}}""";
        (await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), Schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var digits = new string('3', 128);
        string[] distinct =
        [
            "[9007199254740992,9007199254740993]",
            "[1e307,2e307,1e-307,2e-307]",
            $"[{digits},{digits[..^1]}4]",
            """[-0,false,null,true,"0"]""",
            """[{"a":1,"b":2},{"a":2,"b":1}]""",
            "[[1,2],[2,1]]",
            """["é","e\u0301"]""",
        ];
        foreach (var values in distinct)
        {
            (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
                $$"""{"a":{{values}}}""", "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        }

        string[] duplicates =
        [
            "[1,1.0]",
            "[-0,0e300]",
            "[1e-307,10e-308]",
            "[1e307,10e306]",
            $"[{digits},{digits[..^1]}.3e1]",
            """["a","\u0061"]""",
            """[{"a":1,"b":["é",null]},{"b":["\u00e9",null],"\u0061":1.0}]""",
            """[[1,{"x":"a"}],[1.0,{"x":"\u0061"}]]""",
        ];
        foreach (var values in duplicates)
        {
            var id = DocumentApi.NewId();
            await RejectedAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(id),
                $$"""{"a":{{values}}}""", "application/json"));
            (await api.GetAsync(id)).Status.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    private static string SchemaPath(DocumentApi api) => $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";

    private static async Task RejectedAsync(ApiResponse response)
    {
        await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed"));
        await Assert.That(response.Json.GetProperty("error").GetProperty("details").EnumerateArray().Any(detail =>
            detail.GetProperty("path").GetString() == "/a" && detail.GetProperty("keyword").GetString() == "uniqueItems")).IsTrue();
    }
}
