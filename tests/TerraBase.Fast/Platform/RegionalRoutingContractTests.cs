using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

/// <summary>
/// The regional routing contract behind <c>api.terrascale.tech</c> (the ts-orch edge replay contract,
/// region-identity.md, cross-region.md): which region answers, when TerraBase asks the edge to replay a request, and that
/// a replayed request never loops. The edge itself is not in the test: the headers it reads and sets are sent by hand.
/// </summary>
public sealed class RegionalRoutingContractTests
{
    private const string RegionHeader = "X-TerraBase-Region";
    private const string HostPinHeader = "X-TerraScale-Region-Pin";
    private const string Replay = "ts-replay";
    // The edge adds it to a replayed request; TerraBase only checks that it is there.
    private const string ReplaySource = "ts-replay-src";
    private const string ReplayedFromSjc = "region=sjc";
    private static readonly string[] InSjc = ["sjc"];

    [Test]
    public async Task The_answering_node_reports_its_own_region()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        using var anonymous = api.DataClient();

        // Direct requests report the node that actually answered them.
        using var health = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri("/health/replication"));
        using var healthResponse = await anonymous.SendAsync(health);
        using var management = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri("/v1/management/organizations"));
        using var managementResponse = await api.Client.SendAsync(management);

        await Assert.That(healthResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(managementResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        foreach (var response in new[] { healthResponse, managementResponse })
        {
            await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
            await Assert.That(response.Headers.Contains(Replay)).IsFalse();
        }

        // ts-status reads the answering region from the body too; it is the header's.
        using var body = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
        await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("gru");
        await Assert.That(healthResponse.Headers.CacheControl!.ToString()).IsEqualTo("public, max-age=5");
    }

    [Test]
    public async Task A_mismatched_pin_is_replayed_once_and_a_replayed_request_is_answered_where_it_landed()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");

        using var first = await SendPinnedAsync(api.Client, "sjc", replayed: false);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(first.Headers.GetValues(Replay)).IsEquivalentTo(["region=sjc"]);
        await Assert.That(first.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);

        // The edge replayed it here although the pin names sjc (sjc down or refusing): 421 without ts-replay, so no loop.
        using var replayed = await SendPinnedAsync(api.Client, "sjc", replayed: true);
        await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(replayed.Headers.Contains(Replay)).IsFalse();
        await Assert.That(replayed.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
        using (var body = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync()))
        {
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("wrong_region");
        }

        // A replay that lands in the pinned region is served normally.
        using var served = await SendPinnedAsync(api.Client, "gru", replayed: true);
        await Assert.That(served.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(served.Headers.Contains(Replay)).IsFalse();
    }

    [Test]
    public async Task Writes_are_served_by_every_placed_region_and_a_database_placed_elsewhere_is_replayed_to_its_home()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-routing-contract-").FullName;
        try
        {
            string user;
            string organization;
            string project;
            string databases;
            string shared;
            string soloId;
            // The same data directory restarted in another region stands in for that region's copy of the catalog.
            await using (var gru = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "gru";
                settings["data-directory"] = directory;
            }, new OtherRegions("sjc").Register))
            {
                user = gru.User;
                organization = await gru.CreateOrganizationAsync("Routing contract");
                project = await gru.CreateProjectAsync(organization, "Main");
                databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
                using var solo = await gru.Client.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = "solo" }, token);
                await Assert.That(solo.StatusCode).IsEqualTo(HttpStatusCode.Created);
                soloId = (await solo.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
                using var data = gru.DataClient(await gru.Server.IssueAsync(organization, "documents:read", "documents:write"));
                var soloRef = new ManagedDatabaseRef(organization, project, soloId, "solo");
                await ManagementApi.CreateCollectionAsync(data, soloRef, "items", token);
                using var sentinel = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(soloRef, "items", "b")), new { from = "gru" }, token);
                await Assert.That(sentinel.IsSuccessStatusCode).IsTrue();
                using var placed = await gru.Client.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = "shared", replicaRegions = InSjc }, token);
                await Assert.That(placed.StatusCode).IsEqualTo(HttpStatusCode.Created);
                shared = (await placed.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
            }

            await using (var sjc = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "sjc";
                settings["data-directory"] = directory;
            }))
            {
                using var data = sjc.DataClient(await sjc.Server.IssueAsync(organization, "documents:read", "documents:write"));
                var sharedRef = new ManagedDatabaseRef(organization, project, shared, "shared");
                var soloRef = new ManagedDatabaseRef(organization, project, soloId, "solo");

                // Leaderless: a replica region accepts the write itself and reads it locally, with no replay.
                await ManagementApi.CreateCollectionAsync(data, sharedRef, "items", token);
                using (var write = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(sharedRef, "items", "a")), new { from = "sjc" }, token))
                {
                    await Assert.That(write.IsSuccessStatusCode).IsTrue();
                    await Assert.That(write.Headers.GetValues(RegionHeader)).IsEquivalentTo(["sjc"]);
                    await Assert.That(write.Headers.Contains(Replay)).IsFalse();
                }

                using (var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(sharedRef, "items", "a")), token))
                {
                    await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(read.Headers.GetValues(RegionHeader)).IsEquivalentTo(["sjc"]);
                }

                // No write deduplication across regions or retries: Idempotency-Key is refused before the write, so clients
                // must not retry writes and use If-None-Match / If-Match instead.
                using var keyed = new HttpRequestMessage(HttpMethod.Put, ManagementApi.Uri(ManagementApi.DocumentPath(sharedRef, "items", "c")))
                {
                    Content = JsonContent.Create(new { from = "sjc" }),
                };
                keyed.Headers.TryAddWithoutValidation("Idempotency-Key", "01J00000000000000000000000");
                using (var refused = await data.SendAsync(keyed, token))
                {
                    await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                    await Assert.That(await refused.Content.ReadAsStringAsync(token)).Contains("Idempotency-Key is not supported yet");
                }

                using (var absent = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(sharedRef, "items", "c")), token))
                {
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                // A database outside sjc's placement: 421 wrong_region naming its home, and ts-replay to every region serving it.
                using var put = new HttpRequestMessage(HttpMethod.Put, ManagementApi.Uri(ManagementApi.DocumentPath(soloRef, "items", "b")))
                {
                    Content = JsonContent.Create(new { from = "sjc" }),
                };
                using (var misdirected = await data.SendAsync(put, token))
                {
                    await AssertWrongRegionAsync(misdirected, replay: "region=gru", regions: "gru");
                }

                // Replayed to sjc anyway (gru down or refusing): the same 421, without a second replay.
                using var replayedPut = new HttpRequestMessage(HttpMethod.Put, ManagementApi.Uri(ManagementApi.DocumentPath(soloRef, "items", "b")))
                {
                    Content = JsonContent.Create(new { from = "sjc" }),
                };
                replayedPut.Headers.TryAddWithoutValidation(ReplaySource, ReplayedFromSjc);
                using (var replayed = await data.SendAsync(replayedPut, token))
                {
                    await AssertWrongRegionAsync(replayed, replay: null, regions: "gru");
                }

                // Placement changes happen in the home region only, even for a region that serves the database.
                using var owner = sjc.As(user);
                using var patch = await owner.PatchAsJsonAsync(ManagementApi.Uri($"{databases}/{shared}"), new { replicaRegions = Array.Empty<string>() }, token);
                await AssertWrongRegionAsync(patch, replay: "region=gru", regions: "gru,sjc");
            }

            // The refused write never reached the database, and the replica's write is in the shared file.
            await using var back = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "gru";
                settings["data-directory"] = directory;
            });
            using var reader = back.DataClient(await back.Server.IssueAsync(organization, "documents:read"));
            var soloBack = new ManagedDatabaseRef(organization, project, soloId, "solo");
            using (var sentinel = await reader.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(soloBack, "items", "b")), token))
            {
                await Assert.That(sentinel.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var body = await sentinel.Content.ReadFromJsonAsync<JsonElement>(token);
                await Assert.That(body.GetProperty("document").GetProperty("from").GetString()).IsEqualTo("gru");
            }

            using var kept = await reader.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(soloBack with { Name = "shared" }, "items", "a")), token);
            await Assert.That(kept.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(kept.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("gru_1.api.terrascale.tech", null)]
    [Arguments("Not-A-Region-.api.terrascale.tech", null)]
    [Arguments("x9.y9.api.terrascale.tech", null)]
    [Arguments("gru.api.terrascale.tech", "sjc")]
    [Arguments("sjc.api.terrascale.tech", "gru")]
    [Arguments("gru.api.terrascale.tech", "")]
    [Arguments("gru.api.terrascale.tech", "gru,gru")]
    public async Task Rejected_hosts_and_contradictory_pins_precede_authentication_and_mutation(string host, string? pin)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        var organization = await api.CreateOrganizationAsync("Routing refusal");
        var project = await api.CreateProjectAsync(organization, "Main");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        using var anonymous = api.DataClient();
        foreach (var client in new[] { api.Client, anonymous })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ManagementApi.Uri(databases))
            {
                Content = JsonContent.Create(new { name = "rejected" }),
            };
            request.Headers.Host = host;
            if (pin is not null)
            {
                request.Headers.TryAddWithoutValidation(HostPinHeader, pin.Split(','));
            }

            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
            await Assert.That(response.Headers.GetValues("X-TerraBase-Machine")).IsEquivalentTo(["local"]);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("misdirected_request");
        }

        var list = await api.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(databases));
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_empty_regional_label_is_misdirected_before_authentication_and_mutation(bool authenticated)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        var organization = await api.CreateOrganizationAsync("Empty regional label");
        var project = await api.CreateProjectAsync(organization, "Main");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        var authorization = authenticated ? $"Authorization: Bearer {api.Issuer.Mint(api.User)}\r\n" : string.Empty;
        const string payload = "{\"name\":\"rejected\"}";
        // HttpClient rejects this Host or adds a second one; HTTP/1.0 keeps the raw response body unchunked.
        var request = $"POST {databases} HTTP/1.0\r\nHost: .api.terrascale.tech\r\n{authorization}Content-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n{payload}";
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, api.Server.ClientAddress!.Port, token);
        await using var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var response = await reader.ReadToEndAsync(token);
        await Assert.That(response).StartsWith("HTTP/1.1 421 ");
        var parts = response.Split("\r\n\r\n", 2);
        await Assert.That(parts.Length).IsEqualTo(2);
        var headers = parts[0].ToLowerInvariant() + "\r\n";
        await Assert.That(headers).Contains("\r\ncache-control: no-store\r\n");
        await Assert.That(headers).Contains("\r\nx-terrabase-region: gru\r\n");
        await Assert.That(headers).Contains("\r\nx-terrabase-machine: local\r\n");
        using var body = JsonDocument.Parse(parts[1]);
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("misdirected_request");
        var list = await api.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(databases), token);
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    private static async Task<HttpResponseMessage> SendPinnedAsync(HttpClient client, string pin, bool replayed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri("/v1/management/organizations"));
        request.Headers.TryAddWithoutValidation("X-TerraScale-Region-Pin", pin);
        if (replayed)
        {
            request.Headers.TryAddWithoutValidation(ReplaySource, ReplayedFromSjc);
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertWrongRegionAsync(HttpResponseMessage response, string? replay, string regions)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(response.Headers.TryGetValues(Replay, out var replayed) ? replayed.Single() : null).IsEqualTo(replay);
        await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["sjc"]);
        await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Home")).IsEquivalentTo(["gru"]);
        await Assert.That(response.Headers.GetValues("X-TerraBase-Database-Regions")).IsEquivalentTo([regions]);
        await ErrorAssert.ErrorAsync(response, HttpStatusCode.MisdirectedRequest, "wrong_region", TerraBase.Hosting.RegionGuidance.Message("gru", regions.Split(',')));
    }

    // Regional hosts: <region>.api.terrascale.tech pins a region and fails closed (region-identity.md). Several machines
    // run here, behind a fake edge that follows ts-replay as the ts-orch edge does.
    [Test]
    public async Task A_regional_host_is_answered_in_its_own_region_whichever_region_it_lands_in()
    {
        await using var cell = await Cell.StartAsync(["gru", "sjc", "fra"]);
        foreach (var landing in cell.Regions)
        {
            foreach (var target in cell.Regions)
            {
                using var response = await cell.ThroughEdgeAsync(landing, $"{target}.api.terrascale.tech");
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because($"{target}'s host landing in {landing}");
                await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo([target]);
            }

            // Case-insensitive, port ignored; the plain API host stays where it landed (the nearest region).
            using var shouted = await cell.ThroughEdgeAsync(landing, "FRA.Api.TerraScale.Tech:443");
            await Assert.That(shouted.Headers.GetValues(RegionHeader)).IsEquivalentTo(["fra"]);
            using var nearest = await cell.ThroughEdgeAsync(landing, "api.terrascale.tech");
            await Assert.That(nearest.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(nearest.Headers.GetValues(RegionHeader)).IsEquivalentTo([landing]);
        }
    }

    // fra has no started machine: its host is refused with 503 region_unavailable, never answered by gru or sjc, whether
    // the machine list already lacks fra or still lists a machine that is gone (the edge's replay then reaches no machine and,
    // with fallback=self, brings the request back with ts-replay-failed).
    [Test]
    public async Task A_regional_host_of_a_region_without_a_machine_is_refused_and_never_answered_elsewhere()
    {
        await using (var unlisted = await Cell.StartAsync(["gru", "sjc"]))
        {
            using var response = await unlisted.SendAsync("gru", "fra.api.terrascale.tech");
            var text = await AssertRefusedAsync(response, HttpStatusCode.ServiceUnavailable, "region_unavailable", "gru");
            await Assert.That(text).Contains("region fra has no machine to answer; the regional hosts are gru.api.terrascale.tech, sjc.api.terrascale.tech");
        }

        await using var cell = await Cell.StartAsync(["gru", "sjc"], new ClusterMember("fra-gone", "http://127.0.0.1:9/", "fra", string.Empty));
        using (var replayed = await cell.SendAsync("gru", "fra.api.terrascale.tech"))
        {
            await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That(replayed.Headers.GetValues("ts-replay")).IsEquivalentTo(["region=fra;fallback=self"]);
        }

        foreach (var landing in cell.Regions)
        {
            using var response = await cell.ThroughEdgeAsync(landing, "fra.api.terrascale.tech");
            await AssertRefusedAsync(response, HttpStatusCode.ServiceUnavailable, "region_unavailable", landing);
        }
    }

    // A request the edge already replayed that still landed outside its host's region is refused, not answered: no loop, no fallback.
    [Test]
    public async Task A_replayed_request_that_landed_in_another_region_is_refused_without_a_replay()
    {
        await using var cell = await Cell.StartAsync(["gru", "sjc"]);
        using var response = await cell.SendAsync("gru", "sjc.api.terrascale.tech", extra: ("ts-replay-src", "region=sjc"));
        await AssertRefusedAsync(response, HttpStatusCode.ServiceUnavailable, "region_unavailable", "gru");
    }

    [Test]
    [Arguments("gru_1.api.terrascale.tech")]
    [Arguments("Not-A-Region-.api.terrascale.tech")]
    [Arguments("x9.y9.api.terrascale.tech")]
    public async Task A_regional_host_that_names_no_region_is_misdirected_and_lists_the_regional_hosts(string host)
    {
        await using var cell = await Cell.StartAsync(["gru", "sjc"]);
        using var response = await cell.SendAsync("gru", host);
        var text = await AssertRefusedAsync(response, HttpStatusCode.MisdirectedRequest, "misdirected_request", "gru");
        await Assert.That(text).Contains("gru.api.terrascale.tech, sjc.api.terrascale.tech");
        await Assert.That(text).DoesNotContain(host[..host.IndexOf('.', StringComparison.Ordinal)]);
    }

    [Test]
    [Arguments("gru", "sjc")]
    [Arguments("sjc", "gru")]
    [Arguments("gru", "gru,gru")]
    public async Task A_regional_host_and_a_region_pin_that_disagree_are_misdirected_without_a_replay(string host, string pin)
    {
        await using var cell = await Cell.StartAsync(["gru", "sjc"]);
        using var response = await cell.SendAsync("gru", $"{host}.api.terrascale.tech", pin);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(response.Headers.Contains("ts-replay")).IsFalse();
        await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);

        using var agreeing = await cell.SendAsync("gru", "gru.api.terrascale.tech", "gru");
        await Assert.That(agreeing.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_keyed_database_request_to_a_listed_but_gone_region_reports_unavailability()
    {
        await using var cell = await Cell.StartAsync(["gru", "sjc"], new ClusterMember("fra-gone", "http://127.0.0.1:9/", "fra", string.Empty));
        var api = cell.Api("gru");
        using var service = api.Service(api.User);
        var organization = await api.CreateOrganizationAsync("Gone regional host", service);
        var project = await api.CreateProjectAsync(organization, "Main", service);
        var database = new ManagedDatabaseRef(organization, project, await api.CreateDatabaseAsync(organization, project, "main", service), "main");
        using var data = api.DataClient(await api.Server.IssueAsync(database.Organization, "documents:read"));
        foreach (var failed in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "missing")));
            request.Headers.Host = "fra.api.terrascale.tech";
            if (failed)
            {
                request.Headers.Add("ts-replay-failed", "1");
            }

            using var response = await data.SendAsync(request);
            if (failed)
            {
                await AssertRefusedAsync(response, HttpStatusCode.ServiceUnavailable, "region_unavailable", "gru");
            }
            else
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
                await Assert.That(response.Headers.GetValues("ts-replay")).IsEquivalentTo(["region=fra;fallback=self"]);
            }
        }
    }

    private static async Task<string> AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code, string answering)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(response.Headers.Contains("ts-replay")).IsFalse();
        await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo([answering]);
        await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        return text;
    }

    /// <summary>One TerraBase machine per region, listed in one fake machine directory, as the ts-orch peers file lists them.</summary>
    private sealed class Cell : IAsyncDisposable
    {
        private readonly Dictionary<string, ManagementApi> _machines = [with(StringComparer.Ordinal)];
        private readonly List<HttpClient> _clients = [];

        private FakeMemberStore Members { get; } = new();

        public IEnumerable<string> Regions => _machines.Keys;

        public ManagementApi Api(string region) => _machines[region];

        // Every machine is listed before any starts, so no machine reads a list that lacks another (the peers file lists a node
        // once it is started); `gone` are listed machines that do not answer.
        public static async Task<Cell> StartAsync(string[] regions, params ClusterMember[] gone)
        {
            var cell = new Cell();
            foreach (var member in regions.Select(region => new ClusterMember($"{region}-machine", "http://127.0.0.1:9/", region, string.Empty)).Concat(gone))
            {
                cell.Members.Join(member);
            }

            try
            {
                foreach (var region in regions)
                {
                    cell._machines[region] = await ManagementApi.StartAsync(settings =>
                    {
                        settings["region"] = region;
                        settings["node-name"] = $"{region}-machine";
                    }, services => services.AddSingleton<IMemberDirectory>(cell.Members));
                }

                return cell;
            }
            catch
            {
                await cell.DisposeAsync();
                throw;
            }
        }

        // GET /healthz on the region's client listener with this Host, an optional region pin and one more header.
        public async Task<HttpResponseMessage> SendAsync(string region, string host, string? pin = null, (string Name, string Value)? extra = null)
        {
            var client = _machines[region].DataClient();
            _clients.Add(client);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/healthz", UriKind.Relative));
            request.Headers.Host = host;
            if (pin is not null)
            {
                request.Headers.TryAddWithoutValidation(HostPinHeader, pin.Split(','));
            }

            if (extra is var (name, value))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return await client.SendAsync(request);
        }

        // The edge: ts-replay region=R sends the request again to a machine of R, with ts-replay-src; with no machine in R
        // and fallback=self it comes back to the first machine with ts-replay-failed; with no fallback the client gets the
        // original 421. A fallback is not replayed again.
        public async Task<HttpResponseMessage> ThroughEdgeAsync(string landing, string host)
        {
            var response = await SendAsync(landing, host);
            if (!response.Headers.TryGetValues("ts-replay", out var replay))
            {
                return response;
            }

            var fields = replay.Single().Split(';').Select(field => field.Split('=', 2)).ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);
            if (fields["region"].Split(',').FirstOrDefault(_machines.ContainsKey) is { } target)
            {
                response.Dispose();
                return await SendAsync(target, host, extra: ("ts-replay-src", $"region={landing}"));
            }

            if (fields.GetValueOrDefault("fallback") != "self")
            {
                return response;
            }

            response.Dispose();
            return await SendAsync(landing, host, extra: ("ts-replay-failed", "1"));
        }

        public async ValueTask DisposeAsync()
        {
            _clients.ForEach(client => client.Dispose());
            foreach (var machine in _machines.Values)
            {
                await machine.DisposeAsync();
            }
        }
    }
}
