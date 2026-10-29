using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;

namespace TerraBase.Fast.Documents;

public sealed class CollectionAccessRulesTests
{
    private static readonly int[] Vector = [1, 2];

    [Test]
    public async Task Accepted_project_and_end_user_reads_bill_each_returned_document_once()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        await DeclareAsync(api, OwnerRules());
        var actor = DocumentApi.NewId();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { owner = actor, body = new string('x', 10 * 1024) })).Status.ShouldBe(HttpStatusCode.Created);
        for (var i = 0; i < 3; i++)
        {
            (await api.PutAsync(DocumentApi.NewId(), new { owner = actor, body = new string('x', 2049) })).Status.ShouldBe(HttpStatusCode.Created);
        }
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo((0L, 0L));
        using var user = Reader(api, actor);
        foreach (var client in new[] { api.Client, user, api.Client, user })
        {
            var before = await api.ReadUsageAsync();
            await Assert.That((await SendAsync(client, HttpMethod.Get, api.Document(id))).Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await api.ReadUsageAsync()).IsEqualTo((before.Units + 3, before.Reads + 1));
            foreach (var request in new[] { (HttpMethod.Get, api.Documents, (object?)null),
                (HttpMethod.Post, api.Query, (object?)new { count = true }), (HttpMethod.Post, api.Query, (object?)new { explain = true }) })
            {
                before = await api.ReadUsageAsync();
                var page = await SendAsync(client, request.Item1, request.Item2, request.Item3);
                await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(page.Ids.Count).IsEqualTo(4);
                await Assert.That(await api.ReadUsageAsync()).IsEqualTo((before.Units + 6, before.Reads + 1));
            }
        }
    }

    [Test]
    public async Task Rules_are_strict_metadata_preserving_schema_and_ttl()
    {
        await using var source = await CollectionAccessRulesTests.StartAsync();
        var path = Rules(source);
        await Assert.That((await source.SendAsync(HttpMethod.Get, path)).Json.GetProperty("rules").ValueKind).IsEqualTo(JsonValueKind.Null);
        (await source.SendRawAsync(HttpMethod.Put, $"{source.Collections}/items/schema", """{"type":"object"}""", "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        (await source.SendAsync(HttpMethod.Put, $"{source.Collections}/items/ttl", new { path = "/expiresAt" })).Status.ShouldBe(HttpStatusCode.OK);
        foreach (var json in new[]
        {
            "null", "{}", """{"owner":"owner","read":{},"write":{}}""",
            """{"owner":"/id","read":{"owner":true,"roles":[]},"write":{"roles":[]}}""",
            """{"owner":"/id/nested","read":{"owner":true,"roles":[]},"write":{"roles":[]}}""",
            """{"owner":"/owner","read":{"owner":true,"roles":[null]},"write":{"roles":[]}}""",
            """{"owner":"/owner","read":{"roles":[],"script":"true"},"write":{"roles":[]}}""",
            """{"owner":"/owner","owner":"/other","read":{"roles":[]},"write":{"roles":[]}}""",
        })
        {
            var response = await source.SendRawAsync(HttpMethod.Put, path, json, "application/json");
            await Assert.That(response.Status).IsEqualTo(HttpStatusCode.BadRequest).Because(json);
            await Assert.That(response.Code).IsEqualTo("invalid_rules");
        }

        await DeclareAsync(source, OwnerRules());
        await Assert.That((await source.SendAsync(HttpMethod.Get, $"{source.Collections}/items/schema")).Json.GetProperty("schema").GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That((await source.SendAsync(HttpMethod.Get, $"{source.Collections}/items/ttl")).Json.GetProperty("path").GetString()).IsEqualTo("/expiresAt");
        var rules = await source.SendAsync(HttpMethod.Get, Rules(source));
        await Assert.That(rules.Json.GetProperty("rules").GetProperty("owner").GetString()).IsEqualTo("/owner");
    }

    [Test]
    public async Task No_rules_deny_every_data_route_and_hidden_documents_have_no_etag_even_after_project_cache_hits()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        var actor = DocumentApi.NewId();
        var other = DocumentApi.NewId();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { owner = other, secret = "hidden" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.GetAsync(id)).Status.ShouldBe(HttpStatusCode.OK);
        (await api.QueryAsync(new { })).Status.ShouldBe(HttpStatusCode.OK);
        var beforeDenied = await api.ReadUsageAsync();
        using var user = Reader(api, actor);
        foreach (var request in new[] { (HttpMethod.Get, api.Documents), (HttpMethod.Get, api.Document(id)), (HttpMethod.Post, api.Query) })
        {
            var denied = await DocumentApi.SendAsync(user, request.Item1, request.Item2, request.Item1 == HttpMethod.Post ? JsonContent.Create(new { }) : null);
            await Assert.That(denied.Status).IsEqualTo(HttpStatusCode.Forbidden);
        }
        foreach (var deniedId in new[] { id, DocumentApi.NewId() })
        {
            using var patch = new StringContent("""{"value":1}""", Encoding.UTF8, "application/merge-patch+json");
            await Assert.That((await DocumentApi.SendAsync(user, HttpMethod.Patch, api.Document(deniedId), patch)).Status).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That((await SendAsync(user, HttpMethod.Delete, api.Document(deniedId))).Status).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That((await SendAsync(user, HttpMethod.Post, api.Transactions, new { ops = new[] { new { op = "delete", collection = "items", id = deniedId } } })).Status).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That((await SendAsync(user, HttpMethod.Post, api.Transactions, new { ops = new[] { new { op = "assert", collection = "items", id = deniedId, absent = true } } })).Status).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That((await SendAsync(user, HttpMethod.Post, api.Transactions, new { ops = new[] { new { op = "assert", collection = "items", id = deniedId, filter = new { exists = "/owner" } } } })).Status).IsEqualTo(HttpStatusCode.Forbidden);
        }
        var deniedWrite = await SendAsync(user, HttpMethod.Put, api.Document(id), new { owner = actor });
        await Assert.That(deniedWrite.Code).IsEqualTo("access_denied");
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo(beforeDenied);
        await DeclareAsync(api, OwnerRules());
        var visibleVersion = (await api.GetAsync(id)).Version;
        var beforeHidden = await api.ReadUsageAsync();
        var hidden = await DocumentApi.SendAsync(user, HttpMethod.Get, api.Document(id), configure: DocumentApi.IfNoneMatch(visibleVersion));
        await Assert.That(hidden.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(hidden.ETag).IsNull();
        await Assert.That(hidden.Json.GetRawText().Contains("hidden", StringComparison.Ordinal)).IsFalse();
        await Assert.That((await SendAsync(user, HttpMethod.Get, api.Document(DocumentApi.NewId()))).Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo(beforeHidden);
        using var anonymous = api.Management.WithToken(null);
        await Assert.That((await DocumentApi.SendAsync(anonymous, HttpMethod.Get, api.Documents)).Status).IsEqualTo(HttpStatusCode.Unauthorized);
        var otherProject = api.Documents.Replace(api.Tenant.ProjectId, DocumentApi.NewId(), StringComparison.Ordinal);
        await Assert.That((await DocumentApi.SendAsync(user, HttpMethod.Get, otherProject)).Status).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            await Assert.That((await SendAsync(user, method, Rules(api), method == HttpMethod.Put ? OwnerRules() : null)).Status).IsEqualTo(HttpStatusCode.Forbidden);
        }
    }

    [Test]
    public async Task Owner_crud_blocks_claims_transfer_overwrite_patch_delete_and_deleted_id_takeover()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        await DeclareAsync(api, OwnerRules());
        var alice = DocumentApi.NewId();
        var bob = DocumentApi.NewId();
        using var first = Reader(api, alice);
        using var second = Reader(api, bob);
        var id = DocumentApi.NewId();
        await Assert.That((await SendAsync(first, HttpMethod.Put, api.Document(id), new { owner = bob })).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await SendAsync(first, HttpMethod.Put, api.Document(id), new { owner = alice, value = 1 })).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await SendAsync(first, HttpMethod.Put, api.Document(id), new { owner = bob })).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await SendAsync(second, HttpMethod.Put, api.Document(id), new { owner = bob })).Status).IsEqualTo(HttpStatusCode.Forbidden);
        foreach (var client in new[] { first, second })
        {
            using var content = new StringContent($$"""{"owner":"{{bob}}"}""", Encoding.UTF8, "application/merge-patch+json");
            await Assert.That((await DocumentApi.SendAsync(client, HttpMethod.Patch, api.Document(id), content)).Status).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using (var content = new StringContent("""{"value":2}""", Encoding.UTF8, "application/merge-patch+json"))
        {
            await Assert.That((await DocumentApi.SendAsync(first, HttpMethod.Patch, api.Document(id), content)).Status).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That((await SendAsync(second, HttpMethod.Delete, api.Document(id))).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await SendAsync(first, HttpMethod.Delete, api.Document(id))).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await SendAsync(second, HttpMethod.Put, api.Document(id), new { owner = bob })).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await SendAsync(first, HttpMethod.Put, api.Document(id), new { owner = alice })).Status).IsEqualTo(HttpStatusCode.Created);
        var create = await SendAsync(first, HttpMethod.Post, api.Documents, new { owner = alice, value = 3 });
        await Assert.That(create.Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Query_limits_counts_near_and_cursors_are_scoped_before_execution_and_roles_are_case_sensitive()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        var rules = OwnerRules() with { Read = new CollectionAccessPolicy(true, false, ["reader"]) };
        await DeclareAsync(api, rules);
        var actor = DocumentApi.NewId();
        var other = DocumentApi.NewId();
        foreach (var owner in new[] { other, actor, other, actor, other })
        {
            (await api.PutAsync(DocumentApi.NewId(), new { owner, vector = Vector, rank = owner == actor ? 2 : 1 })).Status.ShouldBe(HttpStatusCode.Created);
        }
        using var user = Reader(api, actor);
        using var role = Reader(api, other, "reader");
        using var wrongCase = Reader(api, DocumentApi.NewId(), "Reader");
        var page = await SendAsync(user, HttpMethod.Post, api.Query, new { limit = 1, count = true, sort = new[] { new { path = "/rank", order = "asc" } } });
        await Assert.That(page.Ids.Count).IsEqualTo(1);
        await Assert.That(page.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("2");
        await Assert.That(page.Json.GetProperty("items")[0].GetProperty("document").GetProperty("owner").GetString()).IsEqualTo(actor);
        var cursor = page.Json.GetProperty("next_cursor").GetString();
        var continuation = new { limit = 1, cursor, sort = new[] { new { path = "/rank", order = "asc" } } };
        await Assert.That((await SendAsync(role, HttpMethod.Post, api.Query, continuation)).Code).IsEqualTo("invalid_cursor");
        await Assert.That((await SendAsync(user, HttpMethod.Post, api.Query, continuation)).Ids.Count).IsEqualTo(1);
        await Assert.That((await SendAsync(role, HttpMethod.Post, api.Query, new { count = true })).Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("5");
        await Assert.That((await SendAsync(wrongCase, HttpMethod.Get, api.Documents)).Ids).IsEmpty();
        var near = await SendAsync(user, HttpMethod.Post, api.Query, new { sort = new[] { new { near = new { path = "/vector", vector = Vector } } } });
        await Assert.That(near.Ids.Count).IsEqualTo(2);
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo((9L, 5L));
        await DeclareAsync(api, rules with { Read = new CollectionAccessPolicy(true, true, []) });
        await Assert.That((await SendAsync(user, HttpMethod.Post, api.Query, continuation)).Code).IsEqualTo("invalid_cursor");
        await Assert.That((await SendAsync(user, HttpMethod.Get, api.Documents)).Ids.Count).IsEqualTo(5);
        await DeclareAsync(api, rules with { Read = new CollectionAccessPolicy(false, false, []) });
        await Assert.That((await SendAsync(user, HttpMethod.Get, api.Documents)).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo((14L, 6L));
    }

    [Test]
    public async Task Transactions_scope_assertions_and_rollback_every_write_on_owner_failure()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        await DeclareAsync(api, OwnerRules());
        var actor = DocumentApi.NewId();
        var otherId = DocumentApi.NewId();
        (await api.PutAsync(otherId, new { owner = DocumentApi.NewId() })).Status.ShouldBe(HttpStatusCode.Created);
        using var user = Reader(api, actor);
        var first = DocumentApi.NewId();
        var rejected = await SendAsync(user, HttpMethod.Post, api.Transactions, new
        {
            ops = new object[]
            {
            new { op = "put", collection = "items", id = first, document = new { owner = actor } },
            new { op = "put", collection = "items", id = otherId, document = new { owner = actor } },
            },
        });
        await Assert.That(rejected.Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await api.GetAsync(first)).Status).IsEqualTo(HttpStatusCode.NotFound);
        var asserted = await SendAsync(user, HttpMethod.Post, api.Transactions, new
        {
            ops = new object[]
            {
            new { op = "assert_count", collection = "items", gte = 0, lte = 0 },
            new { op = "put", collection = "items", id = first, document = new { owner = actor } },
            },
        });
        await Assert.That(asserted.Status).IsEqualTo(HttpStatusCode.OK);
        var hiddenAbsent = await SendAsync(user, HttpMethod.Post, api.Transactions, new { ops = new[] { new { op = "assert", collection = "items", id = otherId, absent = true } } });
        await Assert.That(hiddenAbsent.Status).IsEqualTo(HttpStatusCode.OK);
        var deleted = await SendAsync(user, HttpMethod.Post, api.Transactions, new { ops = new[] { new { op = "delete", collection = "items", id = first } } });
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Point_assertions_use_the_authorized_view_without_revealing_hidden_documents()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        await DeclareAsync(api, OwnerRules());
        var actor = DocumentApi.NewId();
        var visible = DocumentApi.NewId();
        var hidden = DocumentApi.NewId();
        var missing = DocumentApi.NewId();
        (await api.PutAsync(visible, new { owner = actor, value = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(hidden, new { owner = DocumentApi.NewId(), value = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        using var user = Reader(api, actor);
        foreach (var id in new[] { hidden, missing })
        {
            var absent = await SendAsync(user, HttpMethod.Post, api.Transactions,
                new { ops = new[] { new { op = "assert", collection = "items", id, absent = true } } });
            await Assert.That(absent.Status).IsEqualTo(HttpStatusCode.OK);
        }

        var visibleAbsent = await SendAsync(user, HttpMethod.Post, api.Transactions,
            new { ops = new[] { new { op = "assert", collection = "items", id = visible, absent = true } } });
        await Assert.That(visibleAbsent.Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        var positive = await SendAsync(user, HttpMethod.Post, api.Transactions,
            new { ops = new[] { new { op = "assert", collection = "items", id = visible, filter = new { eq = new object[] { "/value", 1 } } } } });
        await Assert.That(positive.Status).IsEqualTo(HttpStatusCode.OK);

        string? hiddenFailure = null;
        foreach (var id in new[] { hidden, missing })
        {
            var rolledBack = DocumentApi.NewId();
            var rejected = await SendAsync(user, HttpMethod.Post, api.Transactions, new
            {
                ops = new object[]
                {
                new { op = "put", collection = "items", id = rolledBack, document = new { owner = actor } },
                new { op = "assert", collection = "items", id, filter = new { eq = new object[] { "/value", 1 } } },
                },
            });
            await Assert.That(rejected.Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
            var failure = rejected.Json.GetRawText();
            if (hiddenFailure is null)
            {
                hiddenFailure = failure;
            }
            else
            {
                await Assert.That(failure).IsEqualTo(hiddenFailure);
            }

            await Assert.That((await api.GetAsync(rolledBack)).Status).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task Batched_absence_assertions_do_not_reserve_hidden_document_bodies()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        await DeclareAsync(api, OwnerRules());
        var hiddenOwner = DocumentApi.NewId();
        var hidden = Enumerable.Range(0, 32).Select(_ => DocumentApi.NewId()).ToArray();
        var padding = new string('x', 128 * 1024);
        foreach (var id in hidden)
        {
            (await api.PutAsync(id, new { owner = hiddenOwner, padding })).Status.ShouldBe(HttpStatusCode.Created);
        }

        await api.ReservedBytesAsync();
        using var user = Reader(api, DocumentApi.NewId());
        string[][] batches = [hidden, [.. Enumerable.Range(0, hidden.Length).Select(_ => DocumentApi.NewId())]];
        foreach (var ids in batches)
        {
            var absent = await SendAsync(user, HttpMethod.Post, api.Transactions,
                new { ops = ids.Select(id => new { op = "assert", collection = "items", id, absent = true }).ToArray() });
            await Assert.That(absent.Status).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Role_and_public_write_grants_cover_crud_and_transactions_without_ownership(bool publicWrite)
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        var rules = new CollectionAccessRules(null, new CollectionAccessPolicy(false, true, []),
            new CollectionAccessPolicy(false, publicWrite, publicWrite ? [] : ["writer"]));
        await DeclareAsync(api, rules);
        using var allowed = Reader(api, DocumentApi.NewId(), "writer");
        using var wrongCase = Reader(api, DocumentApi.NewId(), "Writer");
        var id = DocumentApi.NewId();
        var target = api.Document(id);
        var denied = await SendAsync(wrongCase, HttpMethod.Put, target, new { value = 1 });
        await Assert.That(denied.Status).IsEqualTo(publicWrite ? HttpStatusCode.Created : HttpStatusCode.Forbidden);
        var put = await SendAsync(allowed, HttpMethod.Put, target, new { value = 2 });
        await Assert.That(put.Status).IsEqualTo(publicWrite ? HttpStatusCode.OK : HttpStatusCode.Created);
        using (var content = new StringContent("""{"value":3}""", Encoding.UTF8, "application/merge-patch+json"))
        {
            await Assert.That((await DocumentApi.SendAsync(allowed, HttpMethod.Patch, target, content)).Status).IsEqualTo(HttpStatusCode.OK);
        }
        var created = await SendAsync(allowed, HttpMethod.Post, api.Documents, new { value = 4 });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var committed = await SendAsync(allowed, HttpMethod.Post, api.Transactions, new
        {
            ops = new[]
            {
            new { op = "put", collection = "items", id, document = new { value = 5 } },
            },
        });
        await Assert.That(committed.Status).IsEqualTo(HttpStatusCode.OK);
        if (!publicWrite)
        {
            using var patch = new StringContent("""{"value":6}""", Encoding.UTF8, "application/merge-patch+json");
            await Assert.That((await DocumentApi.SendAsync(wrongCase, HttpMethod.Patch, target, patch)).Status).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That((await SendAsync(wrongCase, HttpMethod.Post, api.Documents, new { value = 6 })).Status).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That((await SendAsync(wrongCase, HttpMethod.Delete, target)).Status).IsEqualTo(HttpStatusCode.Forbidden);
            var deniedTransaction = await SendAsync(wrongCase, HttpMethod.Post, api.Transactions, new
            {
                ops = new[]
                {
                new { op = "delete", collection = "items", id },
                },
            });
            await Assert.That(deniedTransaction.Status).IsEqualTo(HttpStatusCode.Forbidden);
        }
        await Assert.That((await SendAsync(allowed, HttpMethod.Delete, target)).Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_role_writer_can_update_another_owners_document_but_cannot_transfer_its_owner()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        await DeclareAsync(api, OwnerRules() with { Write = new CollectionAccessPolicy(true, false, ["writer"]) });
        var owner = DocumentApi.NewId();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { owner, value = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        using var writer = Reader(api, DocumentApi.NewId(), "writer");
        await Assert.That((await SendAsync(writer, HttpMethod.Put, api.Document(id), new { owner, value = 2 })).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await SendAsync(writer, HttpMethod.Put, api.Document(id), new { owner = DocumentApi.NewId() })).Status).IsEqualTo(HttpStatusCode.Forbidden);
        (await api.PutAsync(id, new { owner, value = 2, secret = "private-read-field" })).Status.ShouldBe(HttpStatusCode.OK);
        using (var patch = new StringContent("""{"value":3}""", Encoding.UTF8, "application/merge-patch+json"))
        {
            var patched = await DocumentApi.SendAsync(writer, HttpMethod.Patch, api.Document(id), patch);
            await Assert.That(patched.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(patched.Json.GetRawText().Contains("private-read-field", StringComparison.Ordinal)).IsFalse();
        }
        var patchedTransaction = await SendAsync(writer, HttpMethod.Post, api.Transactions,
            new { ops = new[] { new { op = "patch", collection = "items", id, merge = new { value = 4 } } } });
        await Assert.That(patchedTransaction.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(patchedTransaction.Json.GetRawText().Contains("private-read-field", StringComparison.Ordinal)).IsFalse();
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("secret").GetString()).IsEqualTo("private-read-field");
        var transaction = await SendAsync(writer, HttpMethod.Post, api.Transactions, new
        {
            ops = new[]
            {
            new { op = "put", collection = "items", id, document = new { owner, value = 4 } },
            },
        });
        await Assert.That(transaction.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("owner").GetString()).IsEqualTo(owner);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Anonymous_and_cross_project_identities_cannot_reach_any_data_path(bool crossProject)
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        await DeclareAsync(api, new CollectionAccessRules(null, new CollectionAccessPolicy(false, true, []), new CollectionAccessPolicy(false, true, [])));
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { value = "private-project" })).Status.ShouldBe(HttpStatusCode.Created);
        using var client = crossProject ? Reader(api, DocumentApi.NewId()) : api.Management.WithToken(null);
        var shape = $"{api.Collections}/items/shape";
        var handle = "h1-0000000000000000";
        var requests = new (HttpMethod Method, string Path, object? Body, string? Patch)[]
        {
            (HttpMethod.Get, api.Documents, null, null),
            (HttpMethod.Post, api.Documents, new { value = "claim" }, null),
            (HttpMethod.Get, api.Document(id), null, null),
            (HttpMethod.Put, api.Document(id), new { value = "claim" }, null),
            (HttpMethod.Patch, api.Document(id), null, """{"value":"claim"}"""),
            (HttpMethod.Delete, api.Document(id), null, null),
            (HttpMethod.Post, api.Query, new { count = true }, null),
            (HttpMethod.Post, api.Transactions, new { ops = new[] { new { op = "delete", collection = "items", id } } }, null),
            (HttpMethod.Get, api.DatabasePath() + "/changes", null, null),
            (HttpMethod.Get, shape + "?offset=-1", null, null),
            (HttpMethod.Get, shape + $"/manifest?handle={handle}&offset=0", null, null),
            (HttpMethod.Get, shape + $"/keys?handle={handle}&epoch=0&key_id={DocumentApi.NewId()}", null, null),
        };
        var otherProject = DocumentApi.NewId();
        foreach (var request in requests)
        {
            var path = crossProject ? request.Path.Replace(api.Tenant.ProjectId, otherProject, StringComparison.Ordinal) : request.Path;
            using var content = request.Patch is not null ? new StringContent(request.Patch, Encoding.UTF8, "application/merge-patch+json")
                : request.Body is not null ? (HttpContent)JsonContent.Create(request.Body) : null;
            var response = await DocumentApi.SendAsync(client, request.Method, path, content);
            await Assert.That(response.Status).IsEqualTo(crossProject ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized).Because(request.Method + " " + request.Path);
            await Assert.That(response.ETag).IsNull();
            await Assert.That(response.Json.GetRawText().Contains("private-project", StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    public async Task A_platform_user_session_token_is_not_a_data_credential()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        using var user = api.Management.As(api.Tenant.User);
        var response = await DocumentApi.SendAsync(user, HttpMethod.Get, api.Documents, null);
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Json.GetRawText()).Contains("invalid_key");
    }

    internal static async Task<DocumentApi> StartAsync()
    {
        var api = await DocumentApi.StartAsync();
        try
        {
            using var owner = api.Management.As(api.Tenant.User);
            await api.Management.VerifyProjectBindingAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, owner);
            return api;
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    internal static CollectionAccessRules OwnerRules() => new("/owner", new CollectionAccessPolicy(true, false, []), new CollectionAccessPolicy(true, false, []));
    internal static string Rules(DocumentApi api) => $"{api.Collections}/items/rules";
    internal static async Task DeclareAsync(DocumentApi api, CollectionAccessRules rules) =>
        (await api.SendAsync(HttpMethod.Put, Rules(api), rules)).Status.ShouldBe(HttpStatusCode.OK);
    internal static HttpClient Reader(DocumentApi api, string actor, params string[] roles) =>
        api.Management.WithToken(api.Management.Issuer.Mint(actor, token =>
        {
            token.Claims["token_kind"] = "end_user";
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["instance_id"] = api.Tenant.ProjectId;
            token.Claims["roles"] = roles;
        }));
    internal static Task<ApiResponse> SendAsync(HttpClient client, HttpMethod method, string uri, object? body = null) =>
        DocumentApi.SendAsync(client, method, uri, body is null ? null : JsonContent.Create(body));
}
