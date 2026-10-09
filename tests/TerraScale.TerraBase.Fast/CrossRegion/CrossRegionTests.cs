using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Features.CrossRegion;
using TerraBase.Features.Documents;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.CrossRegion;

/// <summary>
/// Two regions, gru-1 and sfo-1, one member each, replicating over an in-memory region bus with NATS request/reply
/// semantics. Each test registers its database in both regions under the same ids, as an operator does with a
/// management commit (docs/cross-region.md), and reads and writes through the real client listeners.
/// </summary>
public sealed class CrossRegionTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] Scopes = ["documents:read", "documents:write"];
    private static readonly string[] InSfo = ["sfo-1"];
    private static readonly string[] InGru = ["gru-1"];
    private static readonly string[] SfoTwice = ["sfo-1", "sfo-1"];
    private static readonly string[] Unknown = ["iad-1"];

    [Test]
    public async Task Both_regions_meter_encoded_replication_bytes_by_direction_without_charging_client_bandwidth()
    {
        await using var regions = await Regions.StartAsync();
        await regions.GruApi.VerifyProjectBindingAsync(regions.Organization, regions.Project);
        await regions.SfoApi.VerifyProjectBindingAsync(regions.Organization, regions.Project);
        using var gru = Management.UsageAnalyticsTests.ProjectClient(regions.GruApi, regions.Project);
        using var sfo = Management.UsageAnalyticsTests.ProjectClient(regions.SfoApi, regions.Project);
        var value = new Faker().Lorem.Paragraph();
        using var written = await regions.Gru.PutAsJsonAsync(regions.Document(ReplicationDocuments.Id("meter-gru")), new { value });
        written.EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Sfo, regions.Document(ReplicationDocuments.Id("meter-gru")), value);
        using var reverse = await regions.Sfo.PutAsJsonAsync(regions.Document(ReplicationDocuments.Id("meter-sfo")), new { value });
        reverse.EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Gru, regions.Document(ReplicationDocuments.Id("meter-sfo")), value);
        await regions.ConvergedAsync();

        var gruRoute = ManagementApi.Uri($"/projects/{regions.Project}/replication?range=1h&region=gru-1");
        var sfoRoute = ManagementApi.Uri($"/projects/{regions.Project}/replication?range=1h&region=sfo-1");
        var gruUsage = (await Management.UsageAnalyticsTests.ReadAsync(gru, gruRoute)).GetProperty("items")[0];
        var sfoUsage = (await Management.UsageAnalyticsTests.ReadAsync(sfo, sfoRoute)).GetProperty("items")[0];
        await Assert.That(gruUsage.GetProperty("replicatedOps").GetInt64()).IsGreaterThan(0);
        await Assert.That(sfoUsage.GetProperty("replicatedOps").GetInt64()).IsGreaterThan(0);
        await Assert.That(gruUsage.GetProperty("status").GetString()).IsEqualTo("healthy");
        await Assert.That(sfoUsage.GetProperty("lastApplied").ValueKind).IsEqualTo(JsonValueKind.String);
        var sent = Traffic(gruUsage, "gru-1", "sfo-1", "bytesSent");
        var received = Traffic(sfoUsage, "gru-1", "sfo-1", "bytesReceived");
        await Assert.That(sent).IsGreaterThan(0);
        await Assert.That(received).IsEqualTo(sent);
        await Assert.That(Traffic(sfoUsage, "sfo-1", "gru-1", "bytesSent")).IsGreaterThan(0);
        await Assert.That(gruUsage.GetProperty("bandwidth").EnumerateArray().Any(pair => pair.GetProperty("kind").GetString() == "anti_entropy")).IsTrue();
        var analytics = await Management.UsageAnalyticsTests.ReadAsync(gru, ManagementApi.Uri($"/projects/{regions.Project}/analytics?range=1h&region=gru-1"));
        var buckets = analytics.GetProperty("items")[0].GetProperty("series").EnumerateArray().ToArray();
        await Assert.That(buckets.Sum(point => point.GetProperty("replicationBandwidth").GetProperty("bytesSent").GetInt64())).IsGreaterThan(0);
        await Assert.That(buckets.Sum(point => point.GetProperty("clientBandwidth").GetProperty("bytesReceived").GetInt64())).IsGreaterThan(0);

        var outside = await Management.UsageAnalyticsTests.ReadAsync(gru, ManagementApi.Uri($"/projects/{regions.Project}/analytics?range=1h&region=sfo-1"));
        await Assert.That(outside.GetProperty("items").GetArrayLength()).IsEqualTo(0);

        await regions.GruApi.Server.Services.GetRequiredService<TerraBase.Features.Management.Usage.UsageMeter>().FlushAsync(CancellationToken.None);
        var saved = (await Management.UsageAnalyticsTests.ReadAsync(gru, gruRoute)).GetProperty("items")[0];
        await Assert.That(Traffic(saved, "gru-1", "sfo-1", "bytesSent")).IsGreaterThanOrEqualTo(sent);

        // Replication into sfo is never client traffic there: with no sfo document requests while it arrives, client totals
        // stay put as replication grows. Project analytics routes resolve no database, so reading them is not metered.
        var sfoAnalytics = ManagementApi.Uri($"/projects/{regions.Project}/analytics?range=1h&region=sfo-1");
        regions.Network.Partition("gru", "sfo");
        using (var isolated = await regions.Gru.PutAsJsonAsync(regions.Document(ReplicationDocuments.Id("meter-isolated")), new { value }))
        {
            isolated.EnsureSuccessStatusCode();
        }

        var (clientBefore, replicationBefore) = await TotalsAsync();
        var opsBefore = (await Management.UsageAnalyticsTests.ReadAsync(sfo, sfoRoute)).GetProperty("items")[0].GetProperty("replicatedOps").GetInt64();
        regions.Network.Heal();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await sfo.GetFromJsonAsync<JsonElement>(sfoRoute, token)).GetProperty("items")[0].GetProperty("replicatedOps").GetInt64() > opsBefore);
        var (clientAfter, replicationAfter) = await TotalsAsync();
        await Assert.That(clientAfter).IsEqualTo(clientBefore);
        await Assert.That(replicationAfter).IsGreaterThan(replicationBefore);

        async Task<(long Client, long Replication)> TotalsAsync()
        {
            var points = (await Management.UsageAnalyticsTests.ReadAsync(sfo, sfoAnalytics)).GetProperty("items")[0].GetProperty("series").EnumerateArray().ToArray();
            return (points.Sum(point => point.GetProperty("clientBandwidth").GetProperty("bytesSent").GetInt64() + point.GetProperty("clientBandwidth").GetProperty("bytesReceived").GetInt64()),
                points.Sum(point => point.GetProperty("replicationBandwidth").GetProperty("bytesReceived").GetInt64()));
        }

        static long Traffic(JsonElement usage, string source, string destination, string field)
        {
            return usage.GetProperty("bandwidth").EnumerateArray()
                .Where(pair => pair.GetProperty("kind").GetString() is "delta" or "handoff" && pair.GetProperty("sourceRegion").GetString() == source
                    && pair.GetProperty("destinationRegion").GetString() == destination)
                .Sum(pair => pair.GetProperty(field).GetInt64());
        }
    }

    [Test]
    public async Task A_write_in_either_region_is_readable_in_the_other_with_the_same_version()
    {
        await using var regions = await Regions.StartAsync();
        var gruId = ReplicationDocuments.Id("gru");
        var sfoId = ReplicationDocuments.Id("sfo");

        var gruValue = Faker.Lorem.Sentence();
        using var written = await regions.Gru.PutAsJsonAsync(regions.Document(gruId), new { value = gruValue });
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var (_, sfoVersion) = await Regions.EventuallyReadAsync(regions.Sfo, regions.Document(gruId), gruValue);
        // The version token read in one region is the version in the other: it holds as If-Match there.
        await Assert.That(sfoVersion).IsEqualTo(written.Headers.ETag!.Tag);

        var sfoValue = Faker.Lorem.Sentence();
        using var answered = await regions.Sfo.PutAsJsonAsync(regions.Document(sfoId), new { value = sfoValue });
        answered.EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Gru, regions.Document(sfoId), sfoValue);

        var update = Faker.Lorem.Sentence();
        using var conditional = new HttpRequestMessage(HttpMethod.Put, regions.Document(gruId)) { Content = JsonContent.Create(new { value = update }) };
        conditional.Headers.IfMatch.Add(new EntityTagHeaderValue(sfoVersion));
        using var updated = await regions.Sfo.SendAsync(conditional);
        await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        _ = await Regions.EventuallyReadAsync(regions.Gru, regions.Document(gruId), update);

        await TerraBaseTestServer.EventuallyAsync(async token =>
            await regions.GruApi.Server.MetricAsync("terrabase_wan_lag", "region=\"sfo-1\"", token) == 0
            && await regions.SfoApi.Server.MetricAsync("terrabase_wan_lag", "region=\"gru-1\"", token) == 0);
        // The collection and the first document; the update is sfo's own group.
        await Assert.That(await regions.SfoApi.Server.MetricAsync("terrabase_wan_pulled_groups_total", "region=\"gru-1\"")).IsEqualTo(2);
        await Assert.That(await regions.GruApi.Server.MetricAsync("terrabase_wan_members", "region=\"sfo-1\"")).IsEqualTo(1);
    }

    [Test]
    public async Task Partitioned_regions_keep_serving_their_writes_and_converge_after_heal()
    {
        await using var regions = await Regions.StartAsync();
        regions.Network.Partition("gru", "sfo");
        // Both sides went through rounds that reached no member of the other.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await Regions.CountAsync(regions.SfoApi.Server, "terrabase_wan_failures_total", "reason=\"heads\"", token) > 0
            && await Regions.CountAsync(regions.GruApi.Server, "terrabase_wan_failures_total", "reason=\"heads\"", token) > 0);

        var onlyGru = ReplicationDocuments.Id("only-gru");
        var onlySfo = ReplicationDocuments.Id("only-sfo");
        var both = ReplicationDocuments.Id("both");
        (await regions.Gru.PutAsJsonAsync(regions.Document(onlyGru), new { value = "gru" })).EnsureSuccessStatusCode();
        (await regions.Sfo.PutAsJsonAsync(regions.Document(onlySfo), new { value = "sfo" })).EnsureSuccessStatusCode();
        (await regions.Gru.PutAsJsonAsync(regions.Document(both), new { value = "written in gru" })).EnsureSuccessStatusCode();
        (await regions.Sfo.PutAsJsonAsync(regions.Document(both), new { value = "written in sfo" })).EnsureSuccessStatusCode();
        // Each region serves its own writes during the partition.
        await Assert.That((await regions.Gru.GetAsync(regions.Document(onlyGru))).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await regions.Sfo.GetAsync(regions.Document(onlySfo))).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await regions.Sfo.GetAsync(regions.Document(onlyGru))).StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        regions.Network.Heal();
        _ = await Regions.EventuallyReadAsync(regions.Sfo, regions.Document(onlyGru), "gru");
        _ = await Regions.EventuallyReadAsync(regions.Gru, regions.Document(onlySfo), "sfo");
        // The concurrent writes of one document settle on one winner, the same body and version in both regions.
        await regions.ConvergedAsync();
    }

    [Test]
    public async Task Concurrent_writes_in_both_regions_converge_on_the_same_documents()
    {
        await using var regions = await Regions.StartAsync();
        var ids = Enumerable.Range(0, 12).Select(i => ReplicationDocuments.Id($"doc-{i}")).ToArray();
        var values = Enumerable.Range(0, 2 * ids.Length * 4).Select(_ => Faker.Random.Utf16String(1, 64)).ToArray();
        var writes = new List<Task>();
        foreach (var (client, side) in new[] { (regions.Gru, 0), (regions.Sfo, 1) })
        {
            writes.AddRange(ids.Select((id, i) => Task.Run(async () =>
            {
                for (var round = 0; round < 4; round++)
                {
                    using var response = round == 3 && i == 0
                        ? await client.DeleteAsync(regions.Document(id))
                        : await client.PutAsJsonAsync(regions.Document(id), new { value = values[(((side * ids.Length) + i) * 4) + round], round });
                    if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                    {
                        throw new InvalidOperationException($"a write answered {response.StatusCode}");
                    }
                }
            })));
        }

        await Task.WhenAll(writes);
        await regions.ConvergedAsync();
    }

    [Test]
    public async Task A_group_larger_than_one_reply_crosses_in_parts()
    {
        await using var regions = await Regions.StartAsync();
        var id = ReplicationDocuments.Id("large");
        // Random base64url does not compress (nor need JSON escapes): about 1 MB of envelope, more than one NATS reply holds.
        var value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(740_000));
        (await regions.Gru.PutAsJsonAsync(regions.Document(id), new { value })).EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Sfo, regions.Document(id), value);
        await Assert.That(await regions.SfoApi.Server.MetricAsync("terrabase_wan_pulled_encoded_bytes_total", "region=\"gru-1\"")).IsGreaterThan(WanProtocol.ReplyBudget);
    }

    [Test]
    public async Task A_database_of_a_project_only_one_region_registers_stays_there()
    {
        await using var regions = await Regions.StartAsync();
        var project = EntityIds.New().ToString();
        using var service = regions.GruApi.Service(regions.GruApi.User);
        using var registered = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object[] { new { op = "create_project", organization = regions.Organization, id = project, name = "gru-only" } },
        }), Encoding.UTF8, "application/json"));
        registered.EnsureSuccessStatusCode();
        var created = await regions.GruApi.Server.Services.GetRequiredService<ITenantDirectory>()
            .CreateDatabaseAsync(regions.Organization, project, "private", CancellationToken.None);
        var path = $"/v1/projects/{project}/databases/private";
        (await regions.Gru.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        (await regions.Gru.PutAsJsonAsync(new Uri($"{path}/collections/items/documents/by-key?id={ReplicationDocuments.Id("secret")}", UriKind.Relative), new { value = "here" }))
            .EnsureSuccessStatusCode();

        await TerraBaseTestServer.EventuallyAsync(async token =>
            await regions.SfoApi.Server.MetricAsync("terrabase_wan_unregistered", "region=\"gru-1\"", token) == 1);
        var database = created!.Value.Database;
        await Assert.That(File.Exists(Path.Combine(regions.SfoApi.Server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db"))).IsFalse();
    }

    [Test]
    [Arguments("gru-1")]
    [Arguments("sfo-1")]
    public async Task A_database_placed_in_one_region_is_served_there_and_refused_with_its_home_in_the_other(string home)
    {
        await using var regions = await Regions.StartAsync();
        var (homeApi, otherApi) = home == "gru-1" ? (regions.GruApi, regions.SfoApi) : (regions.SfoApi, regions.GruApi);
        var (homeData, otherData) = home == "gru-1" ? (regions.Gru, regions.Sfo) : (regions.Sfo, regions.Gru);
        await otherApi.VerifyProjectBindingAsync(regions.Organization, regions.Project);
        var created = await regions.CreateAsync(homeApi, "orders", home);
        await Assert.That(created.GetProperty("homeRegion").GetString()).IsEqualTo(home);
        await Assert.That(created.GetProperty("replicaRegions").GetArrayLength()).IsEqualTo(0);
        var path = $"/v1/projects/{regions.Project}/databases/orders";
        (await homeData.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        var document = new Uri($"{path}/collections/items/documents/by-key?id={ReplicationDocuments.Id("x")}", UriKind.Relative);
        (await homeData.PutAsJsonAsync(document, new { value = "here" })).EnsureSuccessStatusCode();

        // The other region learns where the database is served, and answers its data requests with that.
        var id = created.GetProperty("id").GetString();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await otherApi.Client.GetAsync(regions.Management($"databases/{id}"), token)).StatusCode == HttpStatusCode.OK);
        var record = await otherApi.Client.GetFromJsonAsync<JsonElement>(regions.Management($"databases/{id}"));
        await Assert.That(record.GetProperty("homeRegion").GetString()).IsEqualTo(home);
        await Regions.WrongRegionAsync(otherData.GetAsync(document), home, home);
        var subscription = new Uri($"/projects/{regions.Project}/databases/orders/collections/items/subscribe", UriKind.Relative);
        foreach (var (scope, expired, expected) in new[]
        {
            ("shapes:read", false, HttpStatusCode.MisdirectedRequest),
            ("documents:read", false, HttpStatusCode.Forbidden),
            ("shapes:read", true, HttpStatusCode.Unauthorized),
        })
        {
            using var subscriber = otherApi.WithToken(otherApi.Issuer.Mint(EntityIds.New().ToString(), token =>
            {
                token.Claims["project_id"] = regions.Project;
                token.Claims["scope"] = scope;
                if (expired)
                {
                    token.IssuedAt = DateTime.UtcNow.AddMinutes(-1);
                    token.Expires = DateTime.UtcNow.AddSeconds(-10);
                }
            }));
            using var answer = await subscriber.GetAsync(subscription);
            await Assert.That(answer.StatusCode).IsEqualTo(expected);
            await Assert.That(answer.Headers.Contains("X-TerraBase-Database-Home")).IsEqualTo(expected == HttpStatusCode.MisdirectedRequest);
        }

        await Regions.WrongRegionAsync(otherData.PutAsJsonAsync(document, new { value = "elsewhere" }), home, home);
        await Regions.WrongRegionAsync(otherApi.Client.PostAsJsonAsync(regions.Management($"databases/{id}/branches"), new { name = "copy" }), home, home);
        using var read = await homeData.GetAsync(document);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_subscription_stops_when_its_region_is_removed_before_a_live_page_is_delivered()
    {
        HeldLivePage? held = null;
        await using var regions = await Regions.StartAsync(services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IShapeService));
            services.Remove(registration);
            services.AddSingleton<ShapeService>();
            services.AddSingleton<IShapeService>(provider => held = new HeldLivePage(provider.GetRequiredService<ShapeService>()));
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await regions.SfoApi.VerifyProjectBindingAsync(regions.Organization, regions.Project);
        using var seed = await regions.Gru.PutAsJsonAsync(regions.Document(ReplicationDocuments.Id("seed")), new { value = "before" }, deadline.Token);
        seed.EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Sfo, regions.Document(ReplicationDocuments.Id("seed")), "before");
        using var subscriber = regions.SfoApi.WithToken(regions.SfoApi.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = regions.Project;
            token.Claims["scope"] = "shapes:read";
        }));
        using var response = await subscriber.GetAsync(new Uri(
            $"/projects/{regions.Project}/databases/canary/collections/items/subscribe", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var snapshot = await ReadSubscriptionEventAsync(reader, deadline.Token);
        await Assert.That(snapshot.Type).IsEqualTo("shape");
        await Assert.That(snapshot.Data).Contains("before");
        while (true)
        {
            using var page = JsonDocument.Parse(snapshot.Data);
            if (page.RootElement.GetProperty("up_to_date").GetBoolean())
            {
                break;
            }

            snapshot = await ReadSubscriptionEventAsync(reader, deadline.Token);
            await Assert.That(snapshot.Type).IsEqualTo("shape");
        }

        try
        {
            using var changed = await regions.Gru.PutAsJsonAsync(regions.Document(ReplicationDocuments.Id("seed")), new { value = "after" }, deadline.Token);
            changed.EnsureSuccessStatusCode();
            // The real feed completed a live read while sfo was still served; hold its result before delivery.
            await held!.Ready.Task.WaitAsync(deadline.Token);
            using var removed = await regions.GruApi.Client.PatchAsJsonAsync(regions.Management($"databases/{regions.Database}"),
                new { replicaRegions = Array.Empty<string>() }, deadline.Token);
            removed.EnsureSuccessStatusCode();
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var answer = await regions.Sfo.GetAsync(regions.Document(ReplicationDocuments.Id("seed")), token);
                return answer.StatusCode == HttpStatusCode.MisdirectedRequest;
            });
            // Removing a replica leaves its local copy intact, so this cannot pass by racing file deletion.
            var file = Path.Combine(regions.SfoApi.Server.DataDirectory, regions.Organization, regions.Project, regions.Database + ".db");
            await Assert.That(File.Exists(file)).IsTrue();
            held.Release.TrySetResult();
            var terminal = await ReadSubscriptionEventAsync(reader, deadline.Token);
            await Assert.That(terminal.Type).IsEqualTo("error");
            using var error = JsonDocument.Parse(terminal.Data);
            await Assert.That(error.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unknown_database");
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsNull();
            await Assert.That(File.Exists(file)).IsTrue();
        }
        finally
        {
            held?.Release.TrySetResult();
        }
    }

    private static async Task<(string Type, string Data)> ReadSubscriptionEventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var type = "";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data.Append(line[6..]).Append('\n');
            }
        }

        return (type, data.ToString());
    }

    private sealed class HeldLivePage(IShapeService inner) : IShapeService
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken, bool drainOnCancellation = false)
        {
            var result = await inner.ReadAsync(request, cancellationToken, drainOnCancellation);
            if (request.Live)
            {
                Ready.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    [Test]
    public async Task A_database_placed_in_both_regions_is_written_and_read_in_either_and_its_placement_follows_its_home()
    {
        await using var regions = await Regions.StartAsync();
        var created = await regions.CreateAsync(regions.GruApi, "orders", "gru-1", "sfo-1");
        var id = created.GetProperty("id").GetString();
        var path = $"/v1/projects/{regions.Project}/databases/orders";
        (await regions.Gru.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        var fromGru = new Uri($"{path}/collections/items/documents/by-key?id={ReplicationDocuments.Id("gru")}", UriKind.Relative);
        var fromSfo = new Uri($"{path}/collections/items/documents/by-key?id={ReplicationDocuments.Id("sfo")}", UriKind.Relative);
        (await regions.Gru.PutAsJsonAsync(fromGru, new { value = "gru" })).EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Sfo, fromGru, "gru");
        (await regions.Sfo.PutAsJsonAsync(fromSfo, new { value = "sfo" })).EnsureSuccessStatusCode();
        _ = await Regions.EventuallyReadAsync(regions.Gru, fromSfo, "sfo");

        // Only the home region changes the placement or deletes; it deletes once no replica is left.
        await Regions.WrongRegionAsync(regions.SfoApi.Client.PatchAsJsonAsync(regions.Management($"databases/{id}"), new { replicaRegions = Array.Empty<string>() }), "gru-1", "gru-1,sfo-1");
        await Regions.WrongRegionAsync(regions.SfoApi.Client.DeleteAsync(regions.Management($"databases/{id}")), "gru-1", "gru-1,sfo-1");
        await ErrorAssert.ErrorAsync(regions.GruApi.Client.DeleteAsync(regions.Management($"databases/{id}")), HttpStatusCode.Conflict, "database_replicated",
            "remove the database's replica regions first");

        // sfo is removed: it stops serving the database at once and keeps its copy, unserved.
        using var removed = await regions.GruApi.Client.PatchAsJsonAsync(regions.Management($"databases/{id}"), new { replicaRegions = Array.Empty<string>() });
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var file = Path.Combine(regions.SfoApi.Server.DataDirectory, regions.Organization, regions.Project, id + ".db");
        await TerraBaseTestServer.EventuallyAsync(async token => (await regions.Sfo.GetAsync(fromGru, token)).StatusCode == HttpStatusCode.MisdirectedRequest);
        await Regions.WrongRegionAsync(regions.Sfo.GetAsync(fromGru), "gru-1", "gru-1");
        await Assert.That(File.Exists(file)).IsTrue();

        // Added back, sfo serves its copy again, with everything both regions wrote.
        using var added = await regions.GruApi.Client.PatchAsJsonAsync(regions.Management($"databases/{id}"), new { replicaRegions = InSfo });
        await Assert.That((await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replicaRegions")[0].GetString()).IsEqualTo("sfo-1");
        _ = await Regions.EventuallyReadAsync(regions.Sfo, fromSfo, "sfo");
        _ = await Regions.EventuallyReadAsync(regions.Sfo, fromGru, "gru");

        // Made local to gru again and deleted there: sfo deletes its record and its copy too.
        using var dropped = await regions.GruApi.Client.PatchAsJsonAsync(regions.Management($"databases/{id}"), new { replicaRegions = Array.Empty<string>() });
        dropped.EnsureSuccessStatusCode();
        using var deleted = await regions.GruApi.Client.DeleteAsync(regions.Management($"databases/{id}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await regions.SfoApi.Client.GetAsync(regions.Management($"databases/{id}"), token)).StatusCode == HttpStatusCode.NotFound && !File.Exists(file));
        using var gone = await regions.Sfo.GetAsync(fromGru);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_placement_is_checked_where_it_is_created_and_through_commits()
    {
        await using var regions = await Regions.StartAsync();
        // Created in its home region only; replicas are regions this deployment replicates with, once each, besides the home.
        await Regions.WrongRegionAsync(regions.GruApi.Client.PostAsJsonAsync(regions.Management("databases"), new { name = "elsewhere", homeRegion = "sfo-1" }), "sfo-1", "sfo-1");
        await ErrorAssert.ErrorAsync(regions.GruApi.Client.PostAsJsonAsync(regions.Management("databases"), new { name = "unknown", replicaRegions = Unknown }),
            HttpStatusCode.BadRequest, "invalid_request", "replicaRegions must name regions this deployment replicates with");
        await ErrorAssert.ErrorAsync(regions.GruApi.Client.PostAsJsonAsync(regions.Management("databases"), new { name = "home", replicaRegions = InGru }),
            HttpStatusCode.BadRequest, "invalid_request", "replicaRegions must not name the home region");
        await ErrorAssert.ErrorAsync(regions.GruApi.Client.PostAsJsonAsync(regions.Management("databases"), new { name = "twice", replicaRegions = SfoTwice }),
            HttpStatusCode.BadRequest, "invalid_request", "replicaRegions must not repeat a region");

        // The same through a commit: created with a replica, then made local to gru.
        var database = EntityIds.New().ToString();
        using var created = await Regions.Commit(regions.GruApi, new { op = "create_database", organization = regions.Organization, project = regions.Project, name = "committed", id = database, replica_regions = InSfo });
        created.EnsureSuccessStatusCode();
        var path = $"/v1/projects/{regions.Project}/databases/committed";
        (await regions.Gru.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await regions.Sfo.GetAsync(new Uri($"{path}/collections/items", UriKind.Relative), token)).StatusCode == HttpStatusCode.OK);
        using var local = await Regions.Commit(regions.GruApi, new { op = "set_database_regions", organization = regions.Organization, project = regions.Project, database, replica_regions = Array.Empty<string>() });
        local.EnsureSuccessStatusCode();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await regions.Sfo.GetAsync(new Uri($"{path}/collections/items", UriKind.Relative), token)).StatusCode == HttpStatusCode.MisdirectedRequest);
        using var refused = await Regions.Commit(regions.SfoApi, new { op = "set_database_regions", organization = regions.Organization, project = regions.Project, database, replica_regions = InGru });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("wrong_region");
    }

    [Test]
    public async Task A_database_deleted_in_its_home_is_not_brought_back_and_a_name_clash_is_counted()
    {
        await using var regions = await Regions.StartAsync();

        // Empty databases are advertised too: partition before creating independent databases with the same name.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await regions.GruApi.Server.MetricAsync("terrabase_wan_members", "region=\"sfo-1\"", token) == 1
            && await regions.SfoApi.Server.MetricAsync("terrabase_wan_members", "region=\"gru-1\"", token) == 1);
        regions.Network.Partition("gru", "sfo");
        // Both sequential pullers have completed any heads request selected before the partition.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await regions.GruApi.Server.MetricAsync("terrabase_wan_members", "region=\"sfo-1\"", token) == 0
            && await regions.SfoApi.Server.MetricAsync("terrabase_wan_members", "region=\"gru-1\"", token) == 0);
        var clash = $"/v1/projects/{regions.Project}/databases/clash";
        using var sfoClash = await regions.Sfo.PutAsync(new Uri(clash, UriKind.Relative), null);
        await Assert.That(sfoClash.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var gruClash = await regions.Gru.PutAsync(new Uri(clash, UriKind.Relative), null);
        await Assert.That(gruClash.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var sfoId = (await sfoClash.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var gruId = (await gruClash.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await Assert.That(gruId).IsNotEqualTo(sfoId);
        (await regions.Gru.PostAsJsonAsync(new Uri($"{clash}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        regions.Network.Heal();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await regions.SfoApi.Server.MetricAsync("terrabase_wan_conflicts", "region=\"gru-1\"", token) == 1);
        using var own = await regions.Sfo.GetAsync(new Uri($"{clash}/collections/items", UriKind.Relative));
        await Assert.That(own.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // An sfo database with a replica in gru is made local to sfo and deleted there: gru deletes its record and copy.
        var created = await regions.CreateAsync(regions.SfoApi, "brief", "sfo-1", "gru-1");
        var id = created.GetProperty("id").GetString();
        var brief = $"/v1/projects/{regions.Project}/databases/brief";
        (await regions.Sfo.PostAsJsonAsync(new Uri($"{brief}/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await regions.Gru.GetAsync(new Uri($"{brief}/collections/items", UriKind.Relative), token)).StatusCode == HttpStatusCode.OK);
        (await regions.SfoApi.Client.PatchAsJsonAsync(regions.Management($"databases/{id}"), new { replicaRegions = Array.Empty<string>() })).EnsureSuccessStatusCode();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await regions.Gru.GetAsync(new Uri($"{brief}/collections/items", UriKind.Relative), token)).StatusCode == HttpStatusCode.MisdirectedRequest);
        using var deleted = await regions.SfoApi.Client.DeleteAsync(regions.Management($"databases/{id}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await regions.GruApi.Client.GetAsync(regions.Management($"databases/{id}"), token)).StatusCode == HttpStatusCode.NotFound);
        using var gone = await regions.Gru.GetAsync(new Uri($"{brief}/collections/items", UriKind.Relative));
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // The catalog tombstone becomes visible before its committed cleanup intent removes the file.
        var file = Path.Combine(regions.GruApi.Server.DataDirectory, regions.Organization, regions.Project, id + ".db");
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!File.Exists(file)));
        // Deleted for good: gru never takes it again, whatever an older head still says.
        await Assert.That(File.Exists(file)).IsFalse();
    }

    [Test]
    public async Task A_commit_registers_a_database_under_a_chosen_id_once()
    {
        await using var regions = await Regions.StartAsync();
        // A new organization and project, but the canary's database id.
        var commit = Regions.Registration(EntityIds.New().ToString(), EntityIds.New().ToString(), regions.Database, "other");
        using var service = regions.GruApi.Service(regions.GruApi.User);
        using var taken = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
        await Assert.That(taken.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var error = (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("id_taken");
        await Assert.That(error.GetProperty("op").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Every_member_of_a_region_reaches_every_member_of_the_other_through_single_replies()
    {
        // gru has two members, a catalog primary and its follower, which replicate to each other over the peer link; the
        // link between regions gives a request one reply, so sfo learns gru-b from gru-a's member list.
        using var peerTls = new PeerTls();
        var network = new FakeRegionNetwork();
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-members-").FullName;
        var credentials = Path.Combine(directory, "user.creds");
        await File.WriteAllTextAsync(credentials, Regions.Credentials);
        var gruMembers = new FakeMemberStore(TimeProvider.System);
        var (portA, portB) = (TerraBaseTestServer.FreePort(), TerraBaseTestServer.FreePort());
        Task<ManagementApi> StartGruAsync(string name, int port)
        {
            return ManagementApi.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                settings["peer-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                settings["peers"] = $"gru-a=localhost:{portA},gru-b=localhost:{portB}";
                settings["catalog-primary"] = "gru-a";
                Regions.Configure(settings, credentials, "gru-1", name);
            }, services => Regions.Join(services, network, "gru", gruMembers, name), managementCredential: true);
        }

        try
        {
            await using var gruA = await StartGruAsync("gru-a", portA);
            await using var gruB = await StartGruAsync("gru-b", portB);
            await using var sfo = await ManagementApi.StartAsync(
                settings => Regions.Configure(settings, credentials, "sfo-1", "sfo-a"),
                services => Regions.Join(services, network, "sfo", new FakeMemberStore(TimeProvider.System)), managementCredential: true);
            var (organization, project, database) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
            foreach (var (api, commit) in new[] { (gruA, Regions.Registration(organization, project, database, "canary", "sfo-1")), (sfo, Regions.Registration(organization, project, null, "canary")) })
            {
                using var service = api.Service(api.User);
                using var registered = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
                registered.EnsureSuccessStatusCode();
            }

            var key = await gruA.Server.IssueAsync(organization, Scopes);
            using var clientA = gruA.DataClient(key);
            using var clientB = gruB.DataClient(key);
            using var clientSfo = await sfo.CreateClientAsync(organization, Scopes);
            var collection = new Uri($"/v1/projects/{project}/databases/canary/collections/items", UriKind.Relative);
            (await clientA.PostAsJsonAsync(new Uri($"/v1/projects/{project}/databases/canary/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                (await clientB.GetAsync(collection, token)).StatusCode == HttpStatusCode.OK && (await clientSfo.GetAsync(collection, token)).StatusCode == HttpStatusCode.OK);

            Uri Document(string id)
            {
                return new($"/v1/projects/{project}/databases/canary/collections/items/documents/by-key?id={ReplicationDocuments.Id(id)}", UriKind.Relative);
            }

            (await clientA.PutAsJsonAsync(Document("from-a"), new { value = "a" })).EnsureSuccessStatusCode();
            (await clientB.PutAsJsonAsync(Document("from-b"), new { value = "b" })).EnsureSuccessStatusCode();
            (await clientSfo.PutAsJsonAsync(Document("from-sfo"), new { value = "sfo" })).EnsureSuccessStatusCode();
            _ = await Regions.EventuallyReadAsync(clientSfo, Document("from-a"), "a");
            _ = await Regions.EventuallyReadAsync(clientSfo, Document("from-b"), "b");
            _ = await Regions.EventuallyReadAsync(clientA, Document("from-sfo"), "sfo");
            _ = await Regions.EventuallyReadAsync(clientB, Document("from-sfo"), "sfo");
            await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", "region=\"gru-1\"")).IsEqualTo(2);

            // gru-b stops answering: sfo is no longer known to be caught up, although nothing it can measure is behind.
            network.Mute("gru-b");
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await sfo.Server.MetricAsync("terrabase_wan_members", "region=\"gru-1\"", token) == 1
                && await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", "region=\"gru-1\"", token) > 3);
            await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", "region=\"gru-1\"")).IsEqualTo(0);
            network.Heal();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", "region=\"gru-1\"", token) < 3);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("gru-1=gru", true)]
    [Arguments("sfo-1=sfo,iad-1=iad", true)]
    [Arguments("gru-1=gru,sfo-1=gru", true)]
    [Arguments("gru-1=GRU,sfo-1=sfo", true)]
    [Arguments("gru-1=gru,sfo-1=sfo", false)]
    public async Task A_node_with_invalid_wan_regions_or_no_nats_does_not_start(string regions, bool nats)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-config-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Regions.Credentials);
            await Assert.That(async () =>
            {
                await using var started = await TerraBaseTestServer.StartAsync(settings =>
                {
                    settings["region"] = "gru-1";
                    settings["node-name"] = "gru-member";
                    settings["wan-regions"] = regions;
                    if (nats)
                    {
                        settings["nats-url"] = "nats://127.0.0.1:4222";
                        settings["nats-credentials-file"] = credentials;
                    }
                });
            }).Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Both regions with one database, "canary", registered in each by a commit with the same organization, project and
    // database ids, a collection "items", and a data client per region.
    private sealed class Regions : IAsyncDisposable
    {
        private readonly string _credentials;

        private Regions(FakeRegionNetwork network, string credentials, ManagementApi gru, ManagementApi sfo)
        {
            (Network, _credentials, GruApi, SfoApi) = (network, credentials, gru, sfo);
        }

        // A NATS user credentials file in shape only: the region bus and member store of these servers are in memory.
        public const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";

        public FakeRegionNetwork Network { get; }

        public ManagementApi GruApi { get; }

        public ManagementApi SfoApi { get; }

        public HttpClient Gru { get; private set; } = null!;

        public HttpClient Sfo { get; private set; } = null!;

        public string Organization { get; } = EntityIds.New().ToString();

        public string Project { get; } = EntityIds.New().ToString();

        public string Database { get; } = EntityIds.New().ToString();

        public Uri Document(string id) => new($"/v1/projects/{Project}/databases/canary/collections/items/documents/by-key?id={id}", UriKind.Relative);

        // A Management route under the canary's project.
        public Uri Management(string path) => new($"/v1/management/organizations/{Organization}/projects/{Project}/{path}", UriKind.Relative);

        // Creates a database through api, which must be its home region's, with the replicas.
        public async Task<JsonElement> CreateAsync(ManagementApi api, string name, string home, params string[] replicas)
        {
            using var created = await api.Client.PostAsJsonAsync(Management("databases"), new { name, homeRegion = home, replicaRegions = replicas });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            return await created.Content.ReadFromJsonAsync<JsonElement>();
        }

        // Sends a commit of one operation through api.
        public static Task<HttpResponseMessage> Commit(ManagementApi api, object op) =>
            api.Client.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(
                JsonSerializer.Serialize(new { id = EntityIds.New().ToString(), ops = new[] { op } }), Encoding.UTF8, "application/json"));

        // The answer of a region outside a database's placement: 421 wrong_region, naming its home and its regions.
        public static async Task WrongRegionAsync(Task<HttpResponseMessage> sending, string home, string regions)
        {
            using var response = await sending;
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("wrong_region");
            await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Home").Single()).IsEqualTo(home);
            await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Regions").Single()).IsEqualTo(regions);
        }

        public static async Task<Regions> StartAsync(Action<IServiceCollection>? sfoServices = null)
        {
            var network = new FakeRegionNetwork();
            var credentials = Path.Combine(Directory.CreateTempSubdirectory("terrabase-wan-").FullName, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials);
            ManagementApi? gru = null;
            try
            {
                gru = await StartRegionAsync(network, credentials, "gru-1", "gru");
                var regions = new Regions(network, credentials, gru, await StartRegionAsync(network, credentials, "sfo-1", "sfo", sfoServices));
                await regions.RegisterAsync();
                return regions;
            }
            catch
            {
                if (gru is not null)
                {
                    await gru.DisposeAsync();
                }

                Directory.Delete(Path.GetDirectoryName(credentials)!, recursive: true);
                throw;
            }
        }

        // An organization, project and database with chosen ids, in one commit.
        public static string Registration(string organization, string project, string? database, string name, params string[] replicas) => JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object?[]
            {
                new { op = "create_organization", id = organization, name = $"canary-{organization}" },
                new { op = "create_project", organization, id = project, name = "canary" },
                database is null ? null : new { op = "create_database", organization, project, name, id = database, replica_regions = replicas },
            }.OfType<object>(),
        });

        // The document at uri once its value is expected, with its version.
        public static async Task<(JsonElement Document, string Version)> EventuallyReadAsync(HttpClient client, Uri uri, string value)
        {
            (JsonElement Document, string Version) read = default;
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var response = await client.GetAsync(uri, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                read = (body.GetProperty("document"), response.Headers.ETag!.Tag);
                return read.Document.GetProperty("value").GetString() == value;
            });
            return read;
        }

        // The samples of a counter, 0 before its first.
        public static async Task<double> CountAsync(TerraBaseTestServer server, string name, string label, CancellationToken token)
        {
            try
            {
                return await server.MetricAsync(name, label, token);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        // Both regions hold the same documents, each with the same body and version, and neither is behind the other.
        public Task ConvergedAsync() =>
            TerraBaseTestServer.EventuallyAsync(async token =>
                await GruApi.Server.MetricAsync("terrabase_wan_lag", "region=\"sfo-1\"", token) == 0
                && await SfoApi.Server.MetricAsync("terrabase_wan_lag", "region=\"gru-1\"", token) == 0
                && await ListAsync(Gru, token) == await ListAsync(Sfo, token));

        public async ValueTask DisposeAsync()
        {
            Gru?.Dispose();
            Sfo?.Dispose();
            try
            {
                await GruApi.DisposeAsync();
            }
            finally
            {
                await SfoApi.DisposeAsync();
                Directory.Delete(Path.GetDirectoryName(_credentials)!, recursive: true);
            }
        }

        // A member of the group gru-1=gru, sfo-1=sfo, on the in-memory region bus and member store.
        public static void Configure(Dictionary<string, string> settings, string credentials, string region, string node)
        {
            settings["region"] = region;
            settings["node-name"] = node;
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }

        public static void Join(IServiceCollection services, FakeRegionNetwork network, string cell, FakeMemberStore members, string? node = null)
        {
            services.AddSingleton(network.Join(cell, node));
            services.AddSingleton<IMemberStore>(members);
        }

        private static Task<ManagementApi> StartRegionAsync(FakeRegionNetwork network, string credentials, string region, string cell, Action<IServiceCollection>? configureServices = null) =>
            ManagementApi.StartAsync(
                settings => Configure(settings, credentials, region, $"{cell}-member"),
                services =>
                {
                    Join(services, network, cell, new FakeMemberStore(TimeProvider.System));
                    configureServices?.Invoke(services);
                }, managementCredential: true);

        // Every document of "items" with its version and body, in id order.
        private async Task<string> ListAsync(HttpClient client, CancellationToken token)
        {
            var listed = new StringBuilder();
            string? cursor = null;
            do
            {
                var page = await client.GetFromJsonAsync<JsonElement>(
                    new Uri($"/v1/projects/{Project}/databases/canary/collections/items/documents?limit=100{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}", UriKind.Relative), token);
                foreach (var document in page.GetProperty("items").EnumerateArray())
                {
                    _ = listed.Append(document.GetProperty("id").GetString()).Append(' ').Append(document.GetProperty("version").GetString())
                        .Append(' ').Append(document.GetProperty("document").GetRawText()).Append('\n');
                }

                cursor = page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            }
            while (cursor is not null);

            return listed.ToString();
        }

        private async Task RegisterAsync()
        {
            // The project in both regions; the canary at home in gru with a replica in sfo, which takes it from gru's heads.
            foreach (var (api, commit) in new[] { (GruApi, Registration(Organization, Project, Database, "canary", "sfo-1")), (SfoApi, Registration(Organization, Project, null, "canary")) })
            {
                using var service = api.Service(api.User);
                using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
            }

            Gru = await GruApi.CreateClientAsync(Organization, Scopes);
            Sfo = await SfoApi.CreateClientAsync(Organization, Scopes);
            (await Gru.PostAsJsonAsync(new Uri($"/v1/projects/{Project}/databases/canary/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                (await Sfo.GetAsync(new Uri($"/v1/projects/{Project}/databases/canary/collections/items", UriKind.Relative), token)).StatusCode == HttpStatusCode.OK);
        }
    }
}
