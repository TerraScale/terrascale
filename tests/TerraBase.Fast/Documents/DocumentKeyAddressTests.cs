using System.Net;

namespace TerraBase.Fast.Documents;

public sealed class DocumentKeyAddressTests
{
    [Test]
    [Arguments("a/b", "a%2Fb")]
    [Arguments(".", "%2E")]
    [Arguments("..", "%2E%2E")]
    [Arguments("%", "?")]
    [Arguments("#", "+")]
    [Arguments("a+b", "a b")]
    [Arguments("雪/😀", "雪%2F😀")]
    [Arguments("é", "e\u0301")]
    public async Task Transaction_keys_remain_distinct_through_conditional_point_CRUD(string key, string other)
    {
        await using var api = await DocumentApi.StartAsync();
        await Assert.That(api.Client.BaseAddress!.Scheme).IsEqualTo("https");
        var seeded = await api.TransactAsync(
            new { op = "put", collection = "items", id = key, document = new { value = "first" } },
            new { op = "put", collection = "items", id = other, document = new { value = "other" } });
        await Assert.That(seeded.Status).IsEqualTo(HttpStatusCode.OK);
        var original = await api.GetAsync(key);
        var untouched = await api.GetAsync(other);
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(original.Json.GetProperty("id").GetString()).IsEqualTo(key);
        await Assert.That(original.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("first");
        await Assert.That(untouched.Json.GetProperty("id").GetString()).IsEqualTo(other);
        await Assert.That(untouched.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("other");
        await Assert.That((await api.GetAsync(key, DocumentApi.IfNoneMatch(original.Version))).Status).IsEqualTo(HttpStatusCode.NotModified);

        var replaced = await api.PutAsync(key, new { value = "replaced" }, DocumentApi.IfMatch(original.Version));
        await Assert.That(replaced.Status).IsEqualTo(HttpStatusCode.OK);
        var patched = await api.SendRawAsync(HttpMethod.Patch, api.Document(key), """{"value":"patched"}""",
            "application/merge-patch+json", DocumentApi.IfMatch(replaced.Version));
        await Assert.That(patched.Status).IsEqualTo(HttpStatusCode.OK);
        var read = await api.GetAsync(key);
        await Assert.That(read.Version).IsEqualTo(patched.Version);
        await Assert.That(read.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("patched");
        var stale = await api.SendAsync(HttpMethod.Delete, api.Document(key), configure: DocumentApi.IfMatch(original.Version));
        await Assert.That(stale.Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(key), configure: DocumentApi.IfMatch(patched.Version));
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        var tombstone = await api.GetAsync(key);
        await Assert.That(tombstone.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(tombstone.ETag).IsEqualTo(deleted.Version);
        var stillOther = await api.GetAsync(other);
        await Assert.That(stillOther.Version).IsEqualTo(untouched.Version);
        await Assert.That(stillOther.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("other");

        var recreated = await api.PutAsync(key, new { value = "recreated" }, DocumentApi.IfMatch(deleted.Version));
        await Assert.That(recreated.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(recreated.Json.GetProperty("id").GetString()).IsEqualTo(key);
        await Assert.That(recreated.Location!.OriginalString).IsEqualTo(api.Document(key));
        var followed = await api.SendAsync(HttpMethod.Get, recreated.Location.OriginalString);
        await Assert.That(followed.Json.GetProperty("id").GetString()).IsEqualTo(key);
        await Assert.That(followed.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("recreated");
    }

    [Test]
    [Arguments("GET")]
    [Arguments("PUT")]
    [Arguments("PATCH")]
    [Arguments("DELETE")]
    public async Task Point_operations_require_one_valid_query_key_and_do_not_accept_path_keys(string verb)
    {
        await using var api = await DocumentApi.StartAsync();
        var key = DocumentApi.NewId();
        var original = await api.PutAsync(key, new { value = 1 });
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.Created);
        var method = new HttpMethod(verb);
        string[] queries = ["", "?id=", $"?id={key}&id={key}", $"?id={key}&x=1", $"?id={key}&ID={key}", "?%69d=key",
            "?id=%FF", "?id=%C0%AF", "?id=%ED%A0%80", "?id=%E2%82", "?id=%", "?id=%2", "?id=%GG", "?id=%00",
            "?id=" + new string('x', 257), "?id=" + new string('x', 256 * 9 + 1)];
        foreach (var query in queries)
        {
            var target = api.Documents + "/by-key" + query;
            // Preserve intentionally malformed transport bytes so HttpClient does not repair the query first.
            var response = await api.SendRawAsync(method, target, """{"value":2}""",
                verb == "PATCH" ? "application/merge-patch+json" : "application/json",
                request => request.RequestUri = new Uri(api.Client.BaseAddress!.AbsoluteUri.TrimEnd('/') + target,
                    new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
            await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request")).Because(target);
        }

        var oldPath = await api.SendRawAsync(method, api.Documents + "/" + key, """{"value":3}""",
            verb == "PATCH" ? "application/merge-patch+json" : "application/json");
        await Assert.That(oldPath.Status).IsEqualTo(HttpStatusCode.NotFound);
        var unchanged = await api.GetAsync(key);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Query_encoding_preserves_the_existing_key_length_boundary_for_multibyte_text()
    {
        await using var api = await DocumentApi.StartAsync();
        var key = new string('雪', 256);
        var created = await api.PutAsync(key, new { value = 1 });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var read = await api.GetAsync(key);
        await Assert.That(read.Json.GetProperty("id").GetString()).IsEqualTo(key);
        var tooLong = await api.PutAsync(key + "雪", new { value = 2 });
        await Assert.That((tooLong.Status, tooLong.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await Assert.That((await api.GetAsync(key)).Version).IsEqualTo(created.Version);
    }

    [Test]
    public async Task Standard_query_plus_and_percent_encoding_decode_once_without_normalizing_keys()
    {
        await using var api = await DocumentApi.StartAsync();
        var seeded = await api.TransactAsync(
            new { op = "put", collection = "items", id = "a b", document = new { value = "space" } },
            new { op = "put", collection = "items", id = "a+b", document = new { value = "plus" } },
            new { op = "put", collection = "items", id = "a/b", document = new { value = "slash" } },
            new { op = "put", collection = "items", id = "a%2fb", document = new { value = "literal" } });
        await Assert.That(seeded.Status).IsEqualTo(HttpStatusCode.OK);
        var formSpace = await api.SendAsync(HttpMethod.Get, api.Documents + "/by-key?id=a+b");
        var escapedSpace = await api.SendAsync(HttpMethod.Get, api.Documents + "/by-key?id=a%20b");
        await Assert.That(formSpace.Json.GetProperty("id").GetString()).IsEqualTo("a b");
        await Assert.That(formSpace.Version).IsEqualTo(escapedSpace.Version);
        var plus = await api.SendAsync(HttpMethod.Get, api.Documents + "/by-key?id=a%2bb");
        await Assert.That(plus.Json.GetProperty("id").GetString()).IsEqualTo("a+b");
        var slash = await api.SendAsync(HttpMethod.Get, api.Documents + "/by-key?id=a%2fb");
        await Assert.That(slash.Json.GetProperty("id").GetString()).IsEqualTo("a/b");
        var literal = await api.SendAsync(HttpMethod.Get, api.Documents + "/by-key?id=a%252fb");
        await Assert.That(literal.Json.GetProperty("id").GetString()).IsEqualTo("a%2fb");
        await Assert.That(literal.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("literal");
    }
}
