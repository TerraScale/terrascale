using System.Net;
using TerraBase.Engine.Lattices;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

/// <summary>
/// Counters and sets in the document API (docs/terrabase/multi-region-replication.md section 13): <c>inc</c>, <c>add</c>
/// and <c>remove</c> write counter and set entries to the document's state, so concurrent writes on different nodes
/// converge once their states are joined.
/// </summary>
public sealed class CounterSetApiTests
{
    private const string Ops = "application/vnd.terrabase.ops+json";

    private const string Shared = "shared";

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
        // An add turns the array into a set: distinct elements in UTF-8 order.
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
            // A set is a JSON array under the document limits: this document holds 4096 values.
            (Element("add", "full", "new"), HttpStatusCode.BadRequest, "invalid_request"),
        ];

        var failures = new List<string>();
        foreach (var (body, status, code) in cases)
        {
            // A valid op first: a refused op fails the whole patch.
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

    // Three nodes start from one stored state, write concurrently through the API, and their states join in any order to
    // one document: increments add up, a concurrent add beats a remove of the same element, and a later set resets.
    [Test]
    public async Task Concurrent_increments_and_set_operations_on_three_nodes_converge()
    {
        await using var gru = await DocumentApi.StartAsync();
        await using var sjc = await DocumentApi.StartAsync();
        await using var fra = await DocumentApi.StartAsync();
        DocumentApi[] nodes = [gru, sjc, fra];
        var id = DocumentApi.NewId();
        var files = await Task.WhenAll(nodes.Select(node => node.ReplaceFileAsync(Shared, path =>
            DocumentFiles.WriteAsync(path, (DocumentApi.Collection, id, """{"n":1,"tags":["x"]}""")))));

        await Task.WhenAll(
            PatchManyAsync(gru, id, [.. Enumerable.Repeat(Inc("n", 1), 5), Element("add", "tags", "x")]),
            PatchManyAsync(sjc, id, [.. Enumerable.Repeat(Inc("n", 1), 3), Element("remove", "tags", "x")]),
            PatchManyAsync(fra, id, [Inc("n", -2), Element("add", "tags", "y")]));
        var states = await Task.WhenAll(files.Select(file => StateAsync(file, id)));
        var sjcAlone = (await sjc.SendAsync(HttpMethod.Get, Document(sjc, id))).Json.GetProperty("document").GetRawText();

        await Assert.That(sjcAlone).IsEqualTo("""{"n":4,"tags":[]}""");
        await Assert.That(Joins(states)).IsEquivalentTo(["""{"n":7,"tags":["x","y"]}"""]);

        // A set after those increments resets the counter: the increments it saw, and every concurrent one, are below it.
        await PatchManyAsync(gru, id, ["""{"op":"set","path":"/n","value":0}"""]);
        await PatchManyAsync(sjc, id, [Inc("n", 10)]);
        var reset = await Task.WhenAll(files.Select(file => StateAsync(file, id)));
        await Assert.That(Joins(reset)).IsEquivalentTo(["""{"n":0,"tags":["x","y"]}"""]);
    }

    private static string Inc(string field, long by) => $$"""{"op":"inc","path":"/{{field}}","by":{{by}}}""";

    private static string Element(string op, string field, string value) => $$"""{"op":"{{op}}","path":"/{{field}}","value":"{{value}}"}""";

    private static string Document(DocumentApi node, string id) =>
        $"{node.DatabasePath(Shared)}/collections/{DocumentApi.Collection}/documents/by-key?id={Uri.EscapeDataString(id)}";

    // One PATCH per op, in order.
    private static async Task PatchManyAsync(DocumentApi node, string id, string[] ops)
    {
        foreach (var op in ops)
        {
            (await node.SendRawAsync(HttpMethod.Patch, Document(node, id), $$"""{"ops":[{{op}}]}""", Ops)).Status.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static async Task<DocumentState> StateAsync(string file, string id) => (await DocumentStateStorageTests.ReadAsync(file, id)).State;

    // The distinct materializations of the three states joined in every order.
    private static HashSet<string> Joins(DocumentState[] states)
    {
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        return [.. orders.Select(order => System.Text.Encoding.UTF8.GetString(
            states[order[0]].Join(states[order[1]]).Join(states[order[2]]).Materialize()!))];
    }
}
