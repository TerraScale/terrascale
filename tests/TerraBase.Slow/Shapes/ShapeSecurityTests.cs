using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Features.Shapes;
using TerraBase.Testing;

namespace TerraBase.Slow.Shapes;

/// <summary>
/// Security review V4 of free shape reads (free-reads.md 3.9, shape-reads.md 11 and 13) over HTTP on one real node:
/// tenancy of the shape and key routes, cache keys and cache policy per response class, and the revocation bound of a
/// live poll admitted before its key was revoked. Every test owns its server and tenants.
/// </summary>
public sealed class ShapeSecurityTests
{
    private const string OpenFilter = """{"eq":["/status","open"]}""";
    private const string ClosedFilter = """{"eq":["/status","closed"]}""";
    private static readonly string[] ReadScopes = ["documents:read"];
    private readonly Faker _faker = new();

    [Test]
    public async Task No_other_organization_project_or_database_key_reaches_a_shape_its_key_or_its_etag()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), Item("open"))).Status).IsEqualTo(HttpStatusCode.Created);
        var shape = ShapeOf(api);
        using var bootstrap = await api.Client.GetAsync(Relative($"{shape}?offset=-1"));
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        var handle = bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
        using var page = await api.Client.GetAsync(bootstrap.Headers.Location);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = page.Headers.ETag!.Tag;

        // Another organization; a key of a sibling project of the same organization; a key of a sibling database.
        using var stranger = await api.Management.CreateTenantAsync("documents:read");
        using var owner = api.Management.As(api.Tenant.User);
        var sibling = await api.Management.CreateProjectAsync(api.Tenant.OrganizationId, "Sibling " + _faker.Random.AlphaNumeric(8), owner);
        using var siblingProject = api.Management.DataClient(await IssueProjectKeyAsync(api.Server.Client, api.Tenant.OrganizationId, sibling, databaseId: null));
        var otherDatabase = await api.SendAsync(HttpMethod.Put, api.DatabasePath("db2"));
        otherDatabase.Status.ShouldBe(HttpStatusCode.Created);
        using var siblingDatabase = api.Management.DataClient(
            await IssueProjectKeyAsync(api.Server.Client, api.Tenant.OrganizationId, api.Tenant.ProjectId, otherDatabase.Json.GetProperty("id").GetString()));

        var keys = $"{shape}/keys?handle={handle}&epoch={CurrentEpoch()}&key_id={DocumentApi.NewId()}";
        foreach (var (who, client) in new[] { ("other organization", stranger.Data), ("sibling project key", siblingProject), ("sibling database key", siblingDatabase) })
        {
            foreach (var target in new[] { $"{shape}?offset=-1", bootstrap.Headers.Location!.OriginalString, $"{shape}?handle={handle}&offset=0", keys })
            {
                foreach (var condition in new[] { etag, "*" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Relative(target));
                    request.Headers.TryAddWithoutValidation("If-None-Match", condition);
                    using var refused = await client.SendAsync(request);
                    var body = await refused.Content.ReadAsStringAsync();
                    var because = $"{who} GET {target} If-None-Match {condition}";
                    await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(because);
                    await Assert.That(Code(body)).IsEqualTo("unknown_database").Because(because);
                    await Assert.That(refused.Headers.CacheControl?.NoStore).IsTrue().Because(because);
                    await Assert.That(refused.Headers.ETag).IsNull().Because(because);
                    await Assert.That(refused.Headers.Contains("TB-Shape-Handle")).IsFalse().Because(because);
                    await Assert.That(body.Contains(handle, StringComparison.Ordinal)).IsFalse().Because(because);
                }
            }
        }
    }

    [Test]
    public async Task A_handle_from_another_tenant_collection_or_filter_is_refused_and_answers_only_the_callers_own_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), Item("open"))).Status).IsEqualTo(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "other" })).Status.ShouldBe(HttpStatusCode.Created);
        var items = ShapeOf(api);
        var other = $"{api.Collections}/other/shape";
        var victim = await BootstrapAsync(api.Client, items);
        var otherCollection = await BootstrapAsync(api.Client, other);
        var filtered = await BootstrapAsync(api.Client, $"{items}?where={Uri.EscapeDataString(OpenFilter)}&");

        using var stranger = await api.Management.CreateTenantAsync("documents:read", "documents:write");
        await CreateCollectionAsync(stranger.Data, stranger.ProjectId);
        var strangerShape = $"/v1/projects/{stranger.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/shape";
        var own = await BootstrapAsync(stranger.Data, strangerShape);
        await Assert.That(own).IsNotEqualTo(victim);

        // (client, target, the caller's own current handle the refusal may name)
        var cases = new[]
        {
            (stranger.Data, $"{strangerShape}?handle={victim}&offset=0", own),
            (stranger.Data, $"{strangerShape}/keys?handle={victim}&epoch={CurrentEpoch()}&key_id={DocumentApi.NewId()}", own),
            (api.Client, $"{items}?handle={otherCollection}&offset=0", victim),
            (api.Client, $"{items}?handle={filtered}&offset=0", victim),
            (api.Client, $"{other}/keys?handle={filtered}&epoch={CurrentEpoch()}&key_id={DocumentApi.NewId()}", otherCollection),
        };
        foreach (var (client, target, current) in cases)
        {
            using var refused = await client.GetAsync(Relative(target));
            var body = await refused.Content.ReadAsStringAsync();
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(target);
            await Assert.That(Code(body)).IsEqualTo("must_refetch").Because(target);
            await Assert.That(refused.Headers.GetValues("TB-Shape-Handle").Single()).IsEqualTo(current).Because(target);
            await Assert.That(refused.Headers.CacheControl?.NoStore).IsTrue().Because(target);
            await Assert.That(body.Contains("\"key\"", StringComparison.Ordinal)).IsFalse().Because(target);
        }

        // Another filter carrying the open filter's handle is refused too; the closed filter was never admitted.
        using var otherFilter = await api.Client.GetAsync(Relative($"{items}?where={Uri.EscapeDataString(ClosedFilter)}&handle={filtered}&offset=0"));
        await Assert.That(otherFilter.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(otherFilter.Headers.GetValues("TB-Shape-Handle").Single()).IsNotEqualTo(filtered);
    }

    [Test]
    public async Task A_database_deleted_and_recreated_under_the_same_name_refuses_the_old_handle_on_the_shape_and_key_routes()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), Item("open"))).Status).IsEqualTo(HttpStatusCode.Created);
        var shape = ShapeOf(api);
        var old = await BootstrapAsync(api.Client, shape);

        await api.DeleteDatabaseAsync();
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath())).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = DocumentApi.Collection })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), Item("open"))).Status).IsEqualTo(HttpStatusCode.Created);
        var current = await BootstrapAsync(api.Client, shape);
        await Assert.That(current).IsNotEqualTo(old);

        foreach (var target in new[]
        {
            $"{shape}?handle={old}&offset=0", $"{shape}?handle={old}&offset=snap.0.0", $"{shape}?handle={old}&offset=0&live=true",
            $"{shape}/keys?handle={old}&epoch={CurrentEpoch()}&key_id={DocumentApi.NewId()}",
        })
        {
            using var refused = await api.Client.GetAsync(Relative(target));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(target);
            await Assert.That(Code(await refused.Content.ReadAsStringAsync())).IsEqualTo("must_refetch").Because(target);
            await Assert.That(refused.Headers.GetValues("TB-Shape-Handle").Single()).IsEqualTo(current).Because(target);
        }
    }

    [Test]
    public async Task Every_shape_response_class_carries_its_cache_policy_and_no_two_tenants_or_filters_share_a_cache_key()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        using var stranger = await api.Management.CreateTenantAsync("documents:read", "documents:write");
        await CreateCollectionAsync(stranger.Data, stranger.ProjectId);
        // Identical documents in both tenants, so only the tenant can tell their responses apart.
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, Item("open"))).Status).IsEqualTo(HttpStatusCode.Created);
        var strangerShape = $"/v1/projects/{stranger.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/shape";
        using (var put = await stranger.Data.PutAsJsonAsync(Relative($"{strangerShape[..^"/shape".Length]}/documents/by-key?id={id}"), Item("open")))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var pages = new List<(string Location, string ETag)>();
        foreach (var (client, shape) in new[]
        {
            (api.Client, ShapeOf(api)), (stranger.Data, strangerShape),
            (api.Client, $"{ShapeOf(api)}?where={Uri.EscapeDataString(OpenFilter)}&"), (api.Client, $"{ShapeOf(api)}?where={Uri.EscapeDataString(ClosedFilter)}&"),
        })
        {
            var target = shape.Contains('?', StringComparison.Ordinal) ? $"{shape}offset=-1" : $"{shape}?offset=-1";
            using var redirect = await client.GetAsync(Relative(target));
            await AssertPrivateAsync(redirect, HttpStatusCode.TemporaryRedirect, target);
            using var page = await client.GetAsync(redirect.Headers.Location);
            await AssertPrivateAsync(page, HttpStatusCode.OK, redirect.Headers.Location!.OriginalString);
            var etag = page.Headers.ETag!.Tag;
            await Assert.That(etag.StartsWith('"') && etag.Contains(":" + redirect.Headers.GetValues("TB-Shape-Handle").Single() + ":", StringComparison.Ordinal)).IsTrue();
            pages.Add((redirect.Headers.Location!.OriginalString, etag));

            using var conditional = new HttpRequestMessage(HttpMethod.Get, redirect.Headers.Location);
            conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var notModified = await client.SendAsync(conditional);
            await AssertPrivateAsync(notModified, HttpStatusCode.NotModified, "304 " + redirect.Headers.Location);
        }

        // Every continuation URL and ETag is distinct: a cache keyed by URL, or by URL and ETag, never mixes them.
        await Assert.That(pages.Select(page => page.Location).Distinct().Count()).IsEqualTo(pages.Count);
        await Assert.That(pages.Select(page => page.ETag).Distinct().Count()).IsEqualTo(pages.Count);

        using var anonymous = api.Management.DataClient();
        using var writeOnly = await api.ClientWithScopesAsync("documents:write");
        var errors = new (HttpClient Client, string Target, HttpStatusCode Status)[]
        {
            (anonymous, $"{ShapeOf(api)}?offset=-1", HttpStatusCode.Unauthorized),
            (writeOnly, $"{ShapeOf(api)}?offset=-1", HttpStatusCode.Forbidden),
            (api.Client, $"{ShapeOf(api)}?offset=bad", HttpStatusCode.BadRequest),
            (api.Client, $"{api.Collections}/missing/shape?offset=-1", HttpStatusCode.NotFound),
            (api.Client, $"{ShapeOf(api)}?handle=h1-0000000000000000&offset=0", HttpStatusCode.Conflict),
            (anonymous, $"{ShapeOf(api)}/keys?handle=h1-0000000000000000&epoch=0&key_id={DocumentApi.NewId()}", HttpStatusCode.Unauthorized),
            (api.Client, $"{ShapeOf(api)}/keys?handle=bad&epoch=0&key_id={DocumentApi.NewId()}", HttpStatusCode.BadRequest),
            (api.Client, $"{ShapeOf(api)}/keys?handle=h1-0000000000000000&epoch=0&key_id={DocumentApi.NewId()}", HttpStatusCode.Conflict),
        };
        foreach (var (client, target, status) in errors)
        {
            using var error = await client.GetAsync(Relative(target));
            await Assert.That(error.StatusCode).IsEqualTo(status).Because(target);
            await Assert.That(error.Headers.CacheControl?.NoStore).IsTrue().Because(target);
            await Assert.That(error.Headers.CacheControl?.Public).IsFalse().Because(target);
            await Assert.That(error.Headers.ETag).IsNull().Because(target);
        }
    }

    [Test]
    public async Task A_live_poll_admitted_before_its_key_is_revoked_delivers_at_most_one_answer_within_the_live_timeout()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), Item("open"))).Status).IsEqualTo(HttpStatusCode.Created);
        var owner = api.Server.Client;
        const string Keys = "/admin/api-keys";
        using var created = await owner.PostAsJsonAsync(Relative(Keys), new { name = _faker.Hacker.Noun(), scopes = ReadScopes, organizationId = api.Tenant.OrganizationId, projectId = api.Tenant.ProjectId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var issued = await created.Content.ReadFromJsonAsync<JsonElement>();
        using var reader = api.Management.DataClient(issued.GetProperty("secret").GetString());
        var shape = ShapeOf(api);
        var (handle, offset) = await SyncAsync(reader, shape);

        var parked = reader.GetAsync(Relative($"{shape}?handle={handle}&offset={offset}&live=true"));
        await TerraBaseTestServer.EventuallyAsync(async ct => parked.IsCompleted || await WaitingAsync(api.Server, ct) >= 1);
        await Assert.That(parked.IsCompleted).IsFalse().Because("the poll must be parked before the revocation");

        using var revoked = await owner.DeleteAsync(Relative($"{Keys}/{issued.GetProperty("id").GetString()}"));
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var sinceRevocation = Stopwatch.StartNew();
        using var refused = await reader.GetAsync(Relative($"{shape}?handle={handle}&offset={offset}"));
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because("a new request is refused at once");

        var written = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(written, Item("open"))).Status).IsEqualTo(HttpStatusCode.Created);
        using var answer = await parked;
        var elapsed = sinceRevocation.Elapsed;
        var body = await answer.Content.ReadAsStringAsync();

        // Measured bound: authorization is checked once, at admission, so a parked poll still answers with a commit made
        // after the revocation, at most the live timeout after it was admitted. Nothing later reaches the holder.
        await Assert.That(answer.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.Contains(written, StringComparison.Ordinal)).IsTrue();
        await Assert.That(elapsed).IsLessThanOrEqualTo(ShapeFlights<ShapeRead>.LiveTimeout);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"V4 revocation: parked poll answered {elapsed.TotalMilliseconds:F0} ms after revocation (bound {ShapeFlights<ShapeRead>.LiveTimeout.TotalSeconds:F0} s)"));

        using var next = await reader.GetAsync(Relative($"{shape}?handle={handle}&offset={answer.Headers.GetValues("TB-Shape-Next-Offset").Single()}&live=true"));
        await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private object Item(string status) => new { status, title = _faker.Lorem.Sentence() };

    private static Uri Relative(string target) => new(target, UriKind.Relative);

    private static string ShapeOf(DocumentApi api) => $"{api.Collections}/{DocumentApi.Collection}/shape";

    private static string CurrentEpoch() => (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600).ToString(CultureInfo.InvariantCulture);

    private static string? Code(string body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
    }

    private static async Task AssertPrivateAsync(HttpResponseMessage response, HttpStatusCode status, string because)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status).Because(because);
        await Assert.That(response.Headers.CacheControl?.Private).IsTrue().Because(because);
        await Assert.That(response.Headers.CacheControl?.Public).IsFalse().Because(because);
        await Assert.That(response.Headers.CacheControl?.SharedMaxAge).IsNull().Because(because);
        await Assert.That(response.Headers.Vary.Contains("Authorization")).IsTrue().Because(because);
    }

    // shape is either "…/shape" or "…/shape?where=…&"; returns the bootstrap's handle.
    private static async Task<string> BootstrapAsync(HttpClient client, string shape)
    {
        var target = shape.EndsWith('&') ? $"{shape}offset=-1" : $"{shape}?offset=-1";
        using var bootstrap = await client.GetAsync(Relative(target));
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        return bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
    }

    private static async Task CreateCollectionAsync(HttpClient client, string projectId)
    {
        using var database = await client.PutAsync(Relative($"/v1/projects/{projectId}/databases/{DocumentApi.Database}"), null);
        database.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var collection = await client.PostAsJsonAsync(Relative($"/v1/projects/{projectId}/databases/{DocumentApi.Database}/collections"), new { name = DocumentApi.Collection });
        collection.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private async Task<string> IssueProjectKeyAsync(HttpClient owner, string organization, string project, string? databaseId)
    {
        using var created = await owner.PostAsJsonAsync(Relative("/admin/api-keys"),
            new { name = _faker.Hacker.Noun(), scopes = ReadScopes, organizationId = organization, projectId = project, databaseId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
    }

    // Bootstraps the newest snapshot, then reads pages and chunks until up to date; returns the handle and the head offset.
    private static async Task<(string Handle, string Offset)> SyncAsync(HttpClient client, string shape)
    {
        using var bootstrap = await client.GetAsync(Relative($"{shape}?offset=-1"));
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        var handle = bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
        var target = bootstrap.Headers.Location!.OriginalString;
        while (true)
        {
            using var page = await client.GetAsync(Relative(target));
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            var offset = page.Headers.GetValues("TB-Shape-Next-Offset").Single();
            if (!offset.StartsWith("snap.", StringComparison.Ordinal) && page.Headers.GetValues("TB-Shape-Up-To-Date").Single() == "true")
            {
                return (handle, offset);
            }

            target = $"{shape}?handle={handle}&offset={offset}";
        }
    }

    // The waiting gauge has no sample until the first reader parks, so an absent gauge reads as zero.
    private static async Task<double> WaitingAsync(TerraBaseTestServer server, CancellationToken token)
    {
        var scrape = await server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);
        return scrape.Split('\n')
            .Where(line => line.StartsWith("terrabase_shape_requests_waiting ", StringComparison.Ordinal)
                || line.StartsWith("terrabase_shape_requests_waiting{", StringComparison.Ordinal))
            .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));
    }
}
