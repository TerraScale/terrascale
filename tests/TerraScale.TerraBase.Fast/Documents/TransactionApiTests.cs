using System.Net;
using System.Text.Json;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class TransactionApiTests
{
    [Test]
    public async Task A_valid_but_failing_count_assertion_rolls_back_without_advancing_the_offset()
    {
        await using var api = await DocumentApi.StartAsync();
        var before = await api.PutAsync(DocumentApi.NewId(), new { n = 1 });
        var fresh = DocumentApi.NewId();
        var result = await api.TransactAsync(
            new { op = "create", collection = "items", id = fresh, document = new { a = 1 } },
            new { op = "assert_count", collection = "items", filter = new { exists = "/a" }, gte = 1 });
        var absent = await api.GetAsync(fresh);
        var after = await api.PutAsync(DocumentApi.NewId(), new { n = 2 });

        await Assert.That((result.Status, result.Code, result.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 1));
        await Assert.That(absent.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Offset(after)).IsEqualTo(Offset(before) + 1);
    }

    [Test]
    public async Task A_transaction_applies_every_operation_with_one_offset()
    {
        await using var api = await DocumentApi.StartAsync();
        var (patched, merged, removed, kept, created) = (DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId());
        await api.PutAsync(patched, new { count = 1 });
        await api.PutAsync(merged, new { name = "old", legacy = true });
        var toRemove = await api.PutAsync(removed, new { a = 1 });
        (await api.SendAsync(HttpMethod.Post, $"{api.Project}/databases/db/collections", new { name = "other" })).Status.ShouldBe(HttpStatusCode.Created);
        var elsewhere = DocumentApi.NewId();
        var before = await api.PutAsync(kept, new { visible = true });

        var result = await api.TransactAsync(
            new { op = "create", collection = "other", id = elsewhere, document = new { other = true } },
            new { op = "create", collection = "items", id = created, document = new { email = "a@b.c" } },
            new { op = "create", collection = "items", document = new { generated = true } },
            new { op = "put", collection = "items", id = DocumentApi.NewId(), document = new { a = 1 }, if_none_match = "*" },
            new { op = "patch", collection = "items", id = patched, ops = new object[] { new { op = "inc", path = "/count", by = 1 } } },
            new { op = "patch", collection = "items", id = merged, merge = new { name = "new", legacy = (object?)null } },
            new { op = "delete", collection = "items", id = removed, if_match = toRemove.Version },
            new { op = "assert", collection = "items", id = kept, filter = new { eq = new object[] { "/visible", true } } },
            new { op = "assert", collection = "items", id = DocumentApi.NewId(), absent = true },
            new { op = "assert_count", collection = "items", filter = new { exists = "/a" }, gte = 1, lte = 1 });
        var after = await api.PutAsync(DocumentApi.NewId(), new { a = 2 });
        var results = result.Json.GetProperty("results").EnumerateArray().Skip(1).ToList();
        var readElsewhere = await api.SendAsync(HttpMethod.Get, $"{api.Project}/databases/db/collections/other/documents/{elsewhere}");
        var readCreated = await api.GetAsync(created);
        var readPatched = await api.GetAsync(patched);
        var readMerged = await api.GetAsync(merged);
        var readRemoved = await api.GetAsync(removed);

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(result.Json.GetProperty("durability").GetString()).IsEqualTo("local_durable");
        // Seven writes in two collections advance the database offset once.
        await Assert.That(Offset(result)).IsEqualTo(Offset(before) + 1);
        await Assert.That(Offset(after)).IsEqualTo(Offset(result) + 1);
        await Assert.That(readElsewhere.Version).IsEqualTo(result.Json.GetProperty("results")[0].GetProperty("version").GetString());
        await Assert.That(results.Count).IsEqualTo(9);
        await Assert.That(results[0].GetProperty("id").GetString()).IsEqualTo(created);
        await Assert.That(results[0].GetProperty("version").GetString()).IsEqualTo(readCreated.Version);
        await Assert.That(Guid.Parse(results[1].GetProperty("id").GetString()!).Version).IsEqualTo(7);
        await Assert.That(results[6].GetRawText()).IsEqualTo("""{"asserted":true}""");
        await Assert.That(results[7].GetRawText()).IsEqualTo("""{"asserted":true}""");
        // assert_count sees the state before this transaction's writes: only the removed document has /a.
        await Assert.That(results[8].GetRawText()).IsEqualTo("""{"asserted":true,"count":"1"}""");
        await Assert.That(readPatched.Json.GetProperty("document").GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(readMerged.Json.GetProperty("document").GetRawText()).IsEqualTo("""{"name":"new"}""");
        await Assert.That(readRemoved.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(readRemoved.ETag).IsEqualTo(results[5].GetProperty("version").GetString());
    }

    [Test]
    public async Task A_failing_operation_rolls_back_the_whole_transaction()
    {
        await using var api = await DocumentApi.StartAsync();
        var (existing, fresh) = (DocumentApi.NewId(), DocumentApi.NewId());
        var original = await api.PutAsync(existing, new { n = 1, a = 1 });

        (await api.SendAsync(HttpMethod.Post, $"{api.Project}/databases/db/collections", new { name = "other" })).Status.ShouldBe(HttpStatusCode.Created);
        var stale = await api.TransactAsync(
            new { op = "create", collection = "other", id = fresh, document = new { a = 1 } },
            new { op = "create", collection = "items", id = fresh, document = new { a = 1 } },
            new { op = "put", collection = "items", id = existing, document = new { n = 2 }, if_match = "tbv1-00000000000000000000000000000000" });
        var exists = await api.TransactAsync(
            new { op = "create", collection = "items", id = fresh, document = new { a = 1 } },
            new { op = "create", collection = "items", id = existing, document = new { n = 4 } });
        var assertion = await api.TransactAsync(
            new { op = "delete", collection = "items", id = existing },
            new { op = "assert", collection = "items", id = fresh, filter = new { exists = "/a" } });
        var conflict = await api.TransactAsync(
            new { op = "create", collection = "items", id = fresh, document = new { a = 1 } },
            new { op = "patch", collection = "items", id = existing, ops = new object[] { new { op = "append", path = "/n", value = 1 } } });
        var missing = await api.TransactAsync(
            new { op = "create", collection = "items", id = fresh, document = new { a = 1 } },
            new { op = "patch", collection = "items", id = DocumentApi.NewId(), merge = new { a = 1 } });
        var unknownCollection = await api.TransactAsync(
            new { op = "create", collection = "items", id = fresh, document = new { a = 1 } },
            new { op = "create", collection = "nope", document = new { a = 1 } });
        var count = await api.TransactAsync(
            new { op = "create", collection = "items", id = fresh, document = new { a = 1 } },
            new { op = "assert_count", collection = "items", filter = new { exists = "/a" }, lte = 0 });
        var absentAfterCount = await api.GetAsync(fresh);
        (await api.PutAsync(fresh, new { a = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        // if_match reads like If-Match: a bare * is the wildcard, a quoted "*" a literal tag, a quoted version a version.
        var quotedStar = await api.TransactAsync(new { op = "put", collection = "items", id = existing, document = new { n = 9 }, if_match = "\"*\"" });
        var unbalanced = await api.TransactAsync(new { op = "put", collection = "items", id = existing, document = new { n = 9 }, if_match = $"\"{original.Version}" });
        var bareStar = await api.TransactAsync(new { op = "put", collection = "items", id = fresh, document = new { a = 2 }, if_match = "*" });
        var freshNow = await api.GetAsync(fresh);
        var quotedVersion = await api.TransactAsync(new { op = "put", collection = "items", id = fresh, document = new { a = 3 }, if_match = $"\"{freshNow.Version}\"" });
        var nullPatchOp = await api.TransactAsync(
            new { op = "create", collection = "items", id = DocumentApi.NewId(), document = new { a = 1 } },
            new { op = "patch", collection = "items", id = existing, ops = new object?[] { null } });
        var readExisting = await api.GetAsync(existing);
        var readFresh = await api.GetAsync(fresh);
        var otherFresh = await api.SendAsync(HttpMethod.Get, $"{api.Project}/databases/db/collections/other/documents/{fresh}");

        await Assert.That((stale.Status, stale.Code, stale.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 2));
        await Assert.That((exists.Status, exists.Code, exists.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 1));
        await Assert.That((assertion.Status, assertion.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, 1));
        await Assert.That((count.Status, count.Code, count.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 1));
        await Assert.That(absentAfterCount.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((conflict.Status, conflict.Code, conflict.Op)).IsEqualTo((HttpStatusCode.Conflict, "patch_conflict", 1));
        await Assert.That((missing.Status, missing.Code, missing.Op)).IsEqualTo((HttpStatusCode.NotFound, "not_found", 1));
        await Assert.That((unknownCollection.Status, unknownCollection.Code, unknownCollection.Op))
            .IsEqualTo((HttpStatusCode.NotFound, "unknown_collection", 1));
        // Null list elements are rejected for every request type at deserialization, with the operation's index.
        await Assert.That((nullPatchOp.Status, nullPatchOp.Code, nullPatchOp.Op)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request", 1));
        await Assert.That((quotedStar.Status, quotedStar.Code, quotedStar.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 0));
        await Assert.That((unbalanced.Status, unbalanced.Code, unbalanced.Op)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request", 0));
        await Assert.That(bareStar.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(quotedVersion.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(readExisting.Version).IsEqualTo(original.Version);
        // Only the explicit standalone write and successful conditional updates committed fresh.
        await Assert.That(readFresh.Status).IsEqualTo(HttpStatusCode.OK);
        // The rolled-back write to the other collection left nothing behind.
        await Assert.That(otherFresh.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    // (body, failing operation index or null when the request as a whole is invalid).
    private static readonly (string Body, int? Op)[] InvalidTransactions =
    [
        ("""{"ops":[]}""", null),
        ("""{"ops":[{"op":"create","collection":"items","document":{},"if_match":null}]}""", 0),
        ("""{"ops":[{"op":"create","collection":"items","document":{},"if_none_match":null}]}""", 0),
        ("""{"ops":[{"op":"create","collection":"items","document":{}},{"op":"patch","collection":"items","id":"00000000-0000-7000-8000-000000000001","merge":{},"ops":null}]}""", 1),
        ("""{"ops":[{"op":"assert","collection":"items","id":"00000000-0000-7000-8000-000000000001","absent":true,"filter":null}]}""", 0),
        ("""{"ops":[{"op":"assert_count","collection":"items","gte":0,"lte":null}]}""", 0),
        ("""{}""", null),
        ("""{"ops":[{"op":"create","collection":"items","document":{}}],"extra":1}""", null),
        ("""{"ops":[{"op":"upsert","collection":"items","id":"00000000-0000-7000-8000-000000000001","document":{}}]}""", 0),
        ("""{"ops":[{"op":"create","collection":"items","document":{},"if_match":"tbv1-x"}]}""", 0),
        ("""{"ops":[{"op":"create","collection":"items","document":[]}]}""", 0),
        ("""{"ops":[{"op":"put","collection":"items","document":{}}]}""", 0),
        ("""{"ops":[{"op":"put","collection":"items","id":"00000000-0000-7000-8000-000000000001","document":{},"if_none_match":"tbv1-x"}]}""", 0),
        ("""{"ops":[{"op":"patch","collection":"items","id":"00000000-0000-7000-8000-000000000001","merge":{},"ops":[{"op":"unset","path":"/a"}]}]}""", 0),
        ("""{"ops":[{"op":"assert","collection":"items","id":"00000000-0000-7000-8000-000000000001"}]}""", 0),
        ("""{"ops":[{"op":"assert_count","collection":"items"}]}""", 0),
        ("""{"ops":[{"op":"assert","collection":"items","id":"00000000-0000-7000-8000-000000000001","filter":{"bad":1}}]}""", 0),
        ("""{"ops":[{"op":"delete","collection":"items","id":"00000000-0000-7000-8000-000000000001"},{"op":"assert","collection":"items","id":"00000000-0000-7000-8000-000000000001","absent":true}]}""", 1),
        ("""{"ops":[null]}""", 0),
        ("""{"ops":[{"op":"create","collection":"items","document":{}},{"op":"assert_count","collection":"items","gte":"1"}]}""", 1),
        ("""{"ops":[{"op":"create","collection":"items","document":{}},{"op":"create","collection":"items","document":{},"extra":1}]}""", 1),
        ("""{"ops":[{"op":"assert_count","collection":"items","gte":-1}]}""", 0),
        ("""{"ops":[{"op":"assert_count","collection":"items","lte":-1}]}""", 0),
        ("""{"ops":[{"op":"assert_count","collection":"items","gte":3,"lte":2}]}""", 0),
        // Duplicates: in an operation they carry its index; in the envelope or a document they are rejected too.
        ("""{"ops":[{"op":"create","collection":"items","document":{}},{"op":"create","op":"create","collection":"items","document":{}}]}""", 1),
        ("""{"ops":[{"op":"create","collection":"items","document":{}},{"op":"create","collection":"items","document":{"a":1,"\u0061":2}}]}""", 1),
        ("""{"ops":[{"op":"create","collection":"items","document":{}}],"ops":[{"op":"create","collection":"items","document":{}}]}""", null),
        ("""{"ops":[{"op":"create","collection":"items","document":{}}]""", null),
        ("""{"ops":[{"op":"delete","collection":"items","id":"00000000-0000-7000-8000-000000000001","if_match":""}]}""", 0),
        ("""{"ops":[{"op":"create","collection":"items","document":{"s":"\ud800"}}]}""", null),
    ];

    [Test]
    public async Task Invalid_transactions_are_rejected_before_any_write()
    {
        await using var api = await DocumentApi.StartAsync();
        var mismatches = new List<string>();
        foreach (var (body, op) in InvalidTransactions)
        {
            var result = await api.SendRawAsync(HttpMethod.Post, api.Transactions, body, "application/json");
            if ((result.Status, result.Code, result.Op) != (HttpStatusCode.BadRequest, "invalid_request", op))
            {
                mismatches.Add($"{body} -> {(int)result.Status} {result.Code} op {result.Op}");
            }
        }

        var listed = await api.SendAsync(HttpMethod.Get, api.Documents);

        await Assert.That(mismatches).IsEmpty();
        await Assert.That(listed.Ids).IsEmpty();
    }

    [Test]
    public async Task Transaction_limits_are_enforced()
    {
        await using var api = await DocumentApi.StartAsync();

        var tooMany = await api.TransactAsync([.. Enumerable.Range(0, 1025).Select(_ => new { op = "create", collection = "items", document = new { } })]);
        var counts = await api.TransactAsync([.. Enumerable.Range(0, 17).Select(_ => new { op = "assert_count", collection = "items", lte = 1 })]);
        var maximum = await api.TransactAsync([.. Enumerable.Range(0, 1024).Select(_ => new { op = "create", collection = "items", document = new { } })]);
        var listed = await api.SendAsync(HttpMethod.Post, api.Query, new { limit = 1, count = true });

        await Assert.That((tooMany.Status, tooMany.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await Assert.That((counts.Status, counts.Op)).IsEqualTo((HttpStatusCode.BadRequest, 16));
        await Assert.That(maximum.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(listed.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("1024");
    }

    [Test]
    public async Task Assertions_need_the_read_scope()
    {
        await using var api = await DocumentApi.StartAsync();
        using var writer = await api.ClientWithScopesAsync("documents:write");

        var assertion = await DocumentApi.SendAsync(
            writer, HttpMethod.Post, api.Transactions,
            JsonContent.Create(new { ops = new object[] { new { op = "assert", collection = "items", id = DocumentApi.NewId(), absent = true } } }));
        var write = await DocumentApi.SendAsync(
            writer, HttpMethod.Post, api.Transactions,
            JsonContent.Create(new { ops = new object[] { new { op = "create", collection = "items", document = new { } } } }));

        await Assert.That((assertion.Status, assertion.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        await Assert.That(write.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(write.Json.GetProperty("offset").ValueKind).IsEqualTo(JsonValueKind.String);
    }

    private static long Offset(ApiResponse response) =>
        long.Parse(response.Json.GetProperty("offset").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
}
