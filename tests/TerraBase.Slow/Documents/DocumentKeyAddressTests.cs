using System.Net;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class DocumentKeyAddressTests
{
    private const string InvalidId = "id must be a canonical ULID: 26 uppercase Crockford base32 characters";

    // Id values that are not canonical ULIDs, as raw query text: caller-chosen keys, other id formats, ULID look-alikes,
    // query encodings (plus, percent, multibyte, malformed UTF-8) and lengths around and far past 26.
    private static readonly string[] InvalidQueryIds =
    [
        "", "customer-key", "doc_existing", "settings", "k1", "01jc9xq1t3v5x7z9b2d4f7hj2k", "00112233-4455-6677-8899-aabbccddeeff",
        "00112233445566778899AABBCCDDEEFF", "org_01JC9XQ1T3V5X7Z9B2D4F7HJ2K", "81JC9XQ1T3V5X7Z9B2D4F7HJ2K", "01JC9XQ1T3V5X7Z9B2D4F7HJ2",
        "01JC9XQ1T3V5X7Z9B2D4F7HJ2KK", "01JC9XQ1T3V5X7Z9B2D4F7HJ2I", "01JC9XQ1T3V5X7Z9B2D4F7HJ2L", "01JC9XQ1T3V5X7Z9B2D4F7HJ2O",
        "01JC9XQ1T3V5X7Z9B2D4F7HJ2U", "01JC9XQ1T3V5X7Z9B2D4F7HJ2-", "+01JC9XQ1T3V5X7Z9B2D4F7HJ2K", "01JC9XQ1T3V5X7Z9B2D4F7HJ2K+",
        "%2001JC9XQ1T3V5X7Z9B2D4F7HJ2K", "%301JC9XQ1T3V5X7Z9B2D4F7HJ2K", "01JC9XQ1T3V5X7Z9B2D4F7HJ2%4B", "a+b", "a%20b", "a%2Fb",
        "a%252fb", "%2E", "%2E%2E", "%E9%9B%AA", "%FF", "%C0%AF", "%ED%A0%80", "%E2%82", "%", "%2", "%GG", "%00",
        new string('x', 257), new string('x', 256 * 9 + 1),
    ];

    [Test]
    public async Task A_ULID_round_trips_through_transactions_and_conditional_point_CRUD()
    {
        await using var api = await DocumentApi.StartAsync();
        await Assert.That(api.Client.BaseAddress!.Scheme).IsEqualTo("http");
        var (key, other) = (DocumentApi.NewId(), DocumentApi.NewId());
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
    public async Task Point_operations_require_one_canonical_ULID_query_id_and_do_not_accept_path_keys(string verb)
    {
        await using var api = await DocumentApi.StartAsync();
        var key = DocumentApi.NewId();
        var original = await api.PutAsync(key, new { value = 1 });
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.Created);
        var method = new HttpMethod(verb);
        var cases = new[] { "", $"?id={key}&id={key}", $"?id={key}&x=1", $"?id={key}&ID={key}", $"?%69d={key}" }
            .Select(query => (Query: query, Code: "invalid_request"))
            .Concat(InvalidQueryIds.Append(key.ToLowerInvariant()).Select(id => (Query: "?id=" + id, Code: "invalid_document_id")));
        foreach (var (query, code) in cases)
        {
            var target = api.Documents + "/by-key" + query;
            // Preserve intentionally malformed transport bytes so HttpClient does not repair the query first.
            var response = await api.SendRawAsync(method, target, """{"value":2}""",
                verb == "PATCH" ? "application/merge-patch+json" : "application/json",
                request => request.RequestUri = new Uri(api.Client.BaseAddress!.AbsoluteUri.TrimEnd('/') + target,
                    new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
            await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.BadRequest, code)).Because(target);
            if (code == "invalid_document_id")
            {
                await Assert.That(response.Message).IsEqualTo(InvalidId).Because(target);
            }
        }

        var oldPath = await api.SendRawAsync(method, api.Documents + "/" + key, """{"value":3}""",
            verb == "PATCH" ? "application/merge-patch+json" : "application/json");
        await Assert.That(oldPath.Status).IsEqualTo(HttpStatusCode.NotFound);
        var unchanged = await api.GetAsync(key);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1);
    }

    [Test]
    [Arguments("create")]
    [Arguments("put")]
    [Arguments("patch")]
    [Arguments("delete")]
    [Arguments("assert")]
    public async Task Every_transaction_operation_refuses_an_id_that_is_not_a_canonical_ULID(string op)
    {
        await using var api = await DocumentApi.StartAsync();
        var existing = DocumentApi.NewId();
        var original = await api.PutAsync(existing, new { value = 1 });
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.Created);
        // JSON ids are not query encoded: the decoded forms of the query cases, plus raw separators and multibyte text.
        string[] ids =
        [
            "", "customer-key", "doc_existing", "01jc9xq1t3v5x7z9b2d4f7hj2k", "00112233-4455-6677-8899-aabbccddeeff",
            "81JC9XQ1T3V5X7Z9B2D4F7HJ2K", "01JC9XQ1T3V5X7Z9B2D4F7HJ2", "01JC9XQ1T3V5X7Z9B2D4F7HJ2KK", "01JC9XQ1T3V5X7Z9B2D4F7HJ2I",
            " 01JC9XQ1T3V5X7Z9B2D4F7HJ2K", "%301JC9XQ1T3V5X7Z9B2D4F7HJ2K", "a/b", "a%2Fb", "a b", "a+b", ".", "..", "%", "?", "#",
            "雪/😀", "é", "é", existing.ToLowerInvariant(), new string('雪', 256),
        ];
        foreach (var id in ids)
        {
            object operation = op switch
            {
                "create" or "put" => new { op, collection = "items", id, document = new { value = 2 } },
                "patch" => new { op, collection = "items", id, merge = new { value = 2 } },
                "delete" => new { op, collection = "items", id },
                _ => new { op, collection = "items", id, absent = true },
            };
            // The invalid operation is second, after a valid write that must roll back with it.
            var result = await api.TransactAsync(new { op = "put", collection = "items", id = existing, document = new { value = 3 } }, operation);
            await Assert.That((result.Status, result.Code, result.Message, result.Op))
                .IsEqualTo((HttpStatusCode.BadRequest, "invalid_document_id", InvalidId, (int?)1)).Because(id);
        }

        var unchanged = await api.GetAsync(existing);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1);
    }
}
