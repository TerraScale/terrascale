using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class ClientAccessShapeTests
{
    [Test]
    public async Task Owner_snapshots_and_handles_are_isolated_even_with_a_broader_user_filter()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await RulesAsync(api, OwnerRules());
        var alice = DocumentApi.NewId();
        var bob = DocumentApi.NewId();
        var own = DocumentApi.NewId();
        var other = DocumentApi.NewId();
        (await api.PutAsync(own, new { owner = alice, status = "open" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(other, new { owner = bob, status = "open" })).Status.ShouldBe(HttpStatusCode.Created);
        using var first = Reader(api, alice);
        using var second = Reader(api, bob);
        var shape = Shape(api);
        var (firstHandle, _, firstIds) = await SyncAsync(first, shape);
        var (secondHandle, _, secondIds) = await SyncAsync(second, shape);
        await Assert.That(firstIds).IsEquivalentTo([own]);
        await Assert.That(secondIds).IsEquivalentTo([other]);
        await Assert.That(firstHandle).IsNotEqualTo(secondHandle);

        var where = $$"""{"or":[{"eq":["/owner","{{bob}}"]},{"eq":["/status","open"]}]}""";
        var (_, _, filtered) = await SyncAsync(first, shape + "?where=" + Uri.EscapeDataString(where));
        await Assert.That(filtered).IsEquivalentTo([own]);
        using var wrongHandle = await first.GetAsync(Relative($"{shape}?handle={secondHandle}&offset=0"));
        await Assert.That(wrongHandle.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(wrongHandle.Headers.GetValues("TB-Shape-Handle").Single()).IsEqualTo(firstHandle);
        await Assert.That((await wrongHandle.Content.ReadAsStringAsync()).Contains(secondHandle, StringComparison.Ordinal)).IsFalse();

        foreach (var target in new[]
        {
            $"{shape}/keys?handle={secondHandle}&epoch=0&key_id={DocumentApi.NewId()}",
            $"{shape}/manifest?handle={secondHandle}&offset=0",
        })
        {
            using var refused = await first.GetAsync(Relative(target));
            await AssertOpaqueDenialAsync(refused, secondHandle);
        }
    }

    [Test]
    public async Task Owner_chunks_emit_only_proven_deletes_and_authorized_afterimages()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await RulesAsync(api, OwnerRules());
        var alice = DocumentApi.NewId();
        var bob = DocumentApi.NewId();
        var deleted = DocumentApi.NewId();
        var transferred = DocumentApi.NewId();
        var received = DocumentApi.NewId();
        var unrelated = DocumentApi.NewId();
        foreach (var (id, owner) in new[] { (deleted, alice), (transferred, alice), (received, bob), (unrelated, bob) })
        {
            (await api.PutAsync(id, new { owner, status = "open" })).Status.ShouldBe(HttpStatusCode.Created);
        }
        using var reader = Reader(api, alice);
        var shape = Shape(api);
        var (handle, offset, _) = await SyncAsync(reader, shape);
        (await api.SendAsync(HttpMethod.Delete, api.Document(deleted))).Status.ShouldBe(HttpStatusCode.OK);
        (await api.PutAsync(transferred, new { owner = bob, status = "open" })).Status.ShouldBe(HttpStatusCode.OK);
        (await api.PutAsync(received, new { owner = alice, status = "open" })).Status.ShouldBe(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Delete, api.Document(unrelated))).Status.ShouldBe(HttpStatusCode.OK);

        var ops = await ChangesAsync(reader, shape, handle, offset);
        await Assert.That(ops.Select(op => op.Id)).IsEquivalentTo([deleted, transferred, received]);
        await Assert.That(ops.Single(op => op.Id == deleted).Op).IsEqualTo("delete");
        await Assert.That(ops.Single(op => op.Id == transferred).Op).IsEqualTo("delete");
        await Assert.That(ops.Single(op => op.Id == received).Op).IsEqualTo("upsert");
    }

    [Test]
    public async Task A_user_where_filter_cannot_authorize_unrelated_deletes_or_old_policy_proofs()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var rules = OwnerRules();
        await RulesAsync(api, rules);
        var actor = DocumentApi.NewId();
        var own = DocumentApi.NewId();
        var other = DocumentApi.NewId();
        (await api.PutAsync(own, new { owner = actor, status = "open" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(other, new { owner = DocumentApi.NewId(), status = "open" })).Status.ShouldBe(HttpStatusCode.Created);
        using var reader = Reader(api, actor);
        var shape = Shape(api) + "?where=" + Uri.EscapeDataString("""{"eq":["/status","open"]}""");
        var (handle, offset, _) = await SyncAsync(reader, shape);
        (await api.PutAsync(own, new { owner = actor, status = "closed" })).Status.ShouldBe(HttpStatusCode.OK);
        (await api.PutAsync(other, new { owner = DocumentApi.NewId(), status = "closed" })).Status.ShouldBe(HttpStatusCode.OK);
        var changes = await ChangesAsync(reader, shape, handle, offset);
        await Assert.That(changes).IsEquivalentTo([(own, "delete")]);
        (await api.SendAsync(HttpMethod.Delete, api.Document(own))).Status.ShouldBe(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Delete, api.Document(other))).Status.ShouldBe(HttpStatusCode.OK);

        await RulesAsync(api, rules with { Write = new CollectionAccessPolicy(true, false, ["writer"]) });
        var (newHandle, _, _) = await SyncAsync(reader, shape);
        await Assert.That(newHandle).IsNotEqualTo(handle);
        await Assert.That(await ChangesAsync(reader, shape, newHandle, "0")).IsEmpty()
            .Because("proofs from another policy cannot authorize historical tombstone IDs");
    }

    [Test]
    public async Task Missing_rules_deny_end_users_while_roles_public_and_legacy_project_access_work()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { value = "visible" })).Status.ShouldBe(HttpStatusCode.Created);
        var shape = Shape(api);
        var (legacy, _, legacyIds) = await SyncAsync(api.Client, shape);
        await Assert.That(legacyIds).IsEquivalentTo([id]);
        using var reader = Reader(api, DocumentApi.NewId(), "reader");
        using var otherCase = Reader(api, DocumentApi.NewId(), "Reader");
        using (var missing = await reader.GetAsync(Relative(shape + "?offset=-1")))
        {
            await AssertOpaqueDenialAsync(missing, legacy);
        }
        var rules = new CollectionAccessRules(null, new CollectionAccessPolicy(false, false, ["reader"]),
            new CollectionAccessPolicy(false, false, []));
        await RulesAsync(api, rules);
        var (roleHandle, _, roleIds) = await SyncAsync(reader, shape);
        await Assert.That(roleIds).IsEquivalentTo([id]);
        await Assert.That(roleHandle).IsNotEqualTo(legacy);
        using (var caseDenied = await otherCase.GetAsync(Relative(shape + "?offset=-1")))
        {
            await AssertOpaqueDenialAsync(caseDenied, roleHandle);
        }
        await RulesAsync(api, rules with { Read = rules.Read! with { Public = true } });
        var (_, _, publicIds) = await SyncAsync(otherCase, shape);
        await Assert.That(publicIds).IsEquivalentTo([id]);
        var (stillLegacy, _, _) = await SyncAsync(api.Client, shape);
        await Assert.That(stillLegacy).IsEqualTo(legacy);
    }

    private static CollectionAccessRules OwnerRules() => new("/owner",
        new CollectionAccessPolicy(true, false, []), new CollectionAccessPolicy(true, false, []));

    private static async Task RulesAsync(DocumentApi api, CollectionAccessRules rules)
    {
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None)
            ?? throw new InvalidOperationException("the database is missing");
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await Assert.That(await transaction.SetCollectionRulesAsync(DocumentApi.Collection, rules, CancellationToken.None)).IsTrue();
        _ = await transaction.CommitAsync(CancellationToken.None);
    }

    private static HttpClient Reader(DocumentApi api, string actor, params string[] roles) =>
        api.Management.WithToken(api.Management.Issuer.Mint(actor, token =>
        {
            token.Claims["token_kind"] = "end_user";
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["instance_id"] = api.Tenant.ProjectId;
            token.Claims["roles"] = roles;
        }));

    private static async Task AssertOpaqueDenialAsync(HttpResponseMessage response, string handle)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(response.Headers.Contains("TB-Shape-Handle")).IsFalse();
        await Assert.That(response.Headers.ETag).IsNull();
        await Assert.That((await response.Content.ReadAsStringAsync()).Contains(handle, StringComparison.Ordinal)).IsFalse();
    }

    private static async Task<(string Handle, string Offset, string[] Ids)> SyncAsync(HttpClient client, string shape)
    {
        using var bootstrap = await client.GetAsync(Relative(Append(shape, "offset=-1")));
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        var handle = bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
        var target = bootstrap.Headers.Location!;
        var ids = new List<string>();
        for (var page = 0; page < 20; page++)
        {
            using var response = await client.GetAsync(target);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            ids.AddRange(Operations(body).Where(op => op.Op == "upsert").Select(op => op.Id));
            var offset = body.GetProperty("next_offset").GetString()!;
            if (body.GetProperty("up_to_date").GetBoolean())
            {
                return (handle, offset, [.. ids]);
            }
            target = Relative(Append(shape, $"handle={handle}&offset={offset}"));
        }
        throw new InvalidOperationException("the shape did not reach its head in 20 pages");
    }

    private static async Task<List<(string Id, string Op)>> ChangesAsync(HttpClient client, string shape, string handle, string offset)
    {
        var ops = new List<(string Id, string Op)>();
        for (var page = 0; page < 20; page++)
        {
            using var response = await client.GetAsync(Relative(Append(shape, $"handle={handle}&offset={offset}")));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            ops.AddRange(Operations(body));
            if (body.GetProperty("up_to_date").GetBoolean())
            {
                return ops;
            }
            offset = body.GetProperty("next_offset").GetString()!;
        }
        throw new InvalidOperationException("the shape did not reach its head in 20 chunks");
    }

    private static IEnumerable<(string Id, string Op)> Operations(JsonElement body) =>
        body.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray())
            .Select(op => (op.GetProperty("id").GetString()!, op.GetProperty("op").GetString()!));

    private static string Shape(DocumentApi api) => $"{api.Collections}/{DocumentApi.Collection}/shape";
    private static string Append(string shape, string query) => shape + (shape.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query;
    private static Uri Relative(string target) => new(target, UriKind.Relative);
}
