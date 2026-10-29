using System.Net;

namespace TerraBase.Fast.Documents;

public sealed class CounterSetApiTests
{
    private const string Ops = "application/vnd.terrabase.ops+json";

    private static readonly string[] Unordered = ["b", "a", "b"];

    private static readonly string[] OneTag = ["a"];

    private static readonly object[] Decrement = [new { op = "inc", path = "/n", by = -4 }];

    [Test]
    public async Task Inc_add_and_remove_answer_the_stored_document_and_its_etag()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1, tags = Unordered })).Status.ShouldBe(HttpStatusCode.Created);

        var patched = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), $$"""
            {"ops":[{{Inc("n", 2)}},{{Element("add", "tags", "c")}},{{Element("remove", "tags", "a")}},{{Element("add", "roles", "x")}}]}
            """, Ops);
        var read = await api.GetAsync(id);
        var transaction = await api.TransactAsync(new { op = "patch", collection = DocumentApi.Collection, id, ops = Decrement });

        patched.Status.ShouldBe(HttpStatusCode.OK);
        const string expected = """{"n":3,"roles":["x"],"tags":["b","c"]}""";
        await Assert.That(patched.Json.GetProperty("document").GetRawText()).IsEqualTo(expected);
        await Assert.That(read.Json.GetProperty("document").GetRawText()).IsEqualTo(expected);
        await Assert.That(read.ETag).IsEqualTo(patched.Version);
        await Assert.That(patched.ETag).IsEqualTo(patched.Version);
        transaction.Status.ShouldBe(HttpStatusCode.OK);
        var result = transaction.Json.GetProperty("results")[0];
        await Assert.That(result.GetProperty("document").GetRawText()).IsEqualTo("""{"n":-1,"roles":["x"],"tags":["b","c"]}""");
        await Assert.That(result.GetProperty("version").GetString()).IsEqualTo((await api.GetAsync(id)).ETag);
    }

    [Test]
    public async Task Invalid_counter_and_set_operations_are_refused_and_change_nothing()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var full = Enumerable.Range(0, 4088).Select(i => i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        (await api.PutAsync(id, new { n = 1, s = "text", f = 1.5, big = long.MaxValue, full, tags = OneTag })).Status.ShouldBe(HttpStatusCode.Created);
        (string Body, HttpStatusCode Status, string Code)[] cases =
        [
            (Inc("profile/visits", 1), HttpStatusCode.BadRequest, "nested_lattice_path"),
            (Element("add", "a/b", "x"), HttpStatusCode.BadRequest, "nested_lattice_path"),
            ("""{"op":"inc","path":"/n","by":1.5}""", HttpStatusCode.BadRequest, "invalid_patch"),
            ("""{"op":"inc","path":"/n","by":0}""", HttpStatusCode.BadRequest, "invalid_patch"),
            ("""{"op":"inc","path":"/n","by":9223372036854775808}""", HttpStatusCode.BadRequest, "invalid_patch"),
            ("""{"op":"inc","path":"/n","by":"1"}""", HttpStatusCode.BadRequest, "invalid_patch"),
            ("""{"op":"add","path":"/tags","value":{"k":1}}""", HttpStatusCode.BadRequest, "invalid_patch"),
            ("""{"op":"remove","path":"/tags","value":[1]}""", HttpStatusCode.BadRequest, "invalid_patch"),
            ("""{"op":"add","path":"/tags"}""", HttpStatusCode.BadRequest, "invalid_patch"),
            (Element("add", "tags", new string('x', 1023)), HttpStatusCode.BadRequest, "invalid_patch"),
            (Inc("s", 1), HttpStatusCode.Conflict, "patch_conflict"),
            (Inc("f", 1), HttpStatusCode.Conflict, "patch_conflict"),
            (Inc("tags", 1), HttpStatusCode.Conflict, "patch_conflict"),
            (Inc("big", 1), HttpStatusCode.Conflict, "patch_conflict"),
            (Element("add", "n", "x"), HttpStatusCode.Conflict, "patch_conflict"),
            (Element("remove", "s", "x"), HttpStatusCode.Conflict, "patch_conflict"),
            (Element("add", "full", "new"), HttpStatusCode.BadRequest, "invalid_request"),
        ];

        var failures = new List<string>();
        foreach (var (body, status, code) in cases)
        {
            var response = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), $$"""{"ops":[{{Inc("n", 1)}},{{body}}]}""", Ops);
            if (response.Status != status || response.Code != code)
            {
                failures.Add($"{body}: {(int)response.Status} {response.Code}, expected {(int)status} {code}");
            }
        }

        var accepted = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), $$"""{"ops":[{{Element("remove", "tags", new string('x', 1022))}}]}""", Ops);
        await Assert.That(failures).IsEmpty();
        accepted.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(accepted.Json.GetProperty("document").GetProperty("n").GetInt64()).IsEqualTo(1);
    }

    private static string Inc(string field, long by) => $$"""{"op":"inc","path":"/{{field}}","by":{{by}}}""";

    private static string Element(string op, string field, string value) => $$"""{"op":"{{op}}","path":"/{{field}}","value":"{{value}}"}""";
}
