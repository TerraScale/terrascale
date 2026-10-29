using TerraBase.Testing;
using System.Net;

namespace TerraBase.Fast.Documents;

public sealed class CounterSetApiTests
{
    private const string Ops = "application/vnd.terrabase.ops+json";

    private static readonly string[] Unordered = ["b", "a", "b"];

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

    private static string Inc(string field, long by) => $$"""{"op":"inc","path":"/{{field}}","by":{{by}}}""";

    private static string Element(string op, string field, string value) => $$"""{"op":"{{op}}","path":"/{{field}}","value":"{{value}}"}""";
}
