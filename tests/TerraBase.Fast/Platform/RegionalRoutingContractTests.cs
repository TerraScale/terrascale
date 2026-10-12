using System.Net;
using System.Text.Json;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

/// <summary>
/// The regional routing contract behind <c>api.terrascale.tech</c> (Fly anycast; docs/terrabase/fly.md#fly-replay,
/// region-identity.md, cross-region.md): which region answers, when TerraBase asks Fly Proxy to replay a request, and that
/// a replayed request never loops. Fly Proxy itself is not in the test: the headers it reads and sets are sent by hand.
/// </summary>
public sealed class RegionalRoutingContractTests
{
    private const string RegionHeader = "X-TerraBase-Region";
    private const string Replay = "fly-replay";
    // Fly Proxy adds it to a replayed request; its exact value does not matter to TerraBase.
    private const string ReplaySource = "Fly-Replay-Src";
    private const string ReplayedFromSjc = "instance=d894655b3ed118;region=sjc;t=1760227200000000";
    private static readonly string[] InSjc = ["sjc"];

    [Test]
    public async Task Fly_prefer_region_is_fly_proxys_choice_and_the_answering_node_reports_its_own_region()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        using var anonymous = api.DataClient();

        // Fly Proxy honors fly-prefer-region before TerraBase sees the request; a node it lands on answers as itself.
        using var health = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri("/health/replication"));
        health.Headers.Add("fly-prefer-region", "sjc");
        using var healthResponse = await anonymous.SendAsync(health);
        using var management = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri("/v1/management/organizations"));
        management.Headers.Add("fly-prefer-region", "sjc");
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

        // Fly replayed it here although the pin names sjc (sjc down or refusing): 421 without fly-replay, so no loop.
        using var replayed = await SendPinnedAsync(api.Client, "sjc", replayed: true);
        await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(replayed.Headers.Contains(Replay)).IsFalse();
        await Assert.That(replayed.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru"]);
        using (var body = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync()))
        {
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("misdirected_request");
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
            // The same data directory restarted in another region stands in for that region's copy of the catalog.
            await using (var gru = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = "gru";
                settings["data-directory"] = directory;
            }))
            {
                user = gru.User;
                organization = await gru.CreateOrganizationAsync("Routing contract");
                project = await gru.CreateProjectAsync(organization, "Main");
                databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
                using var solo = await gru.Client.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = "solo" }, token);
                await Assert.That(solo.StatusCode).IsEqualTo(HttpStatusCode.Created);
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
                var soloRef = sharedRef with { Name = "solo" };

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

                // A database outside sjc's placement: 421 wrong_region naming its home, and fly-replay there.
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
            var soloBack = new ManagedDatabaseRef(organization, project, EntityIds.New().ToString(), "solo");
            using (var missing = await reader.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(soloBack, "items", "b")), token))
            {
                await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
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
        await ErrorAssert.ErrorAsync(response, HttpStatusCode.MisdirectedRequest, "wrong_region", "the database is served in gru");
    }
}
