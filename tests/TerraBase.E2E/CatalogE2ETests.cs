using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// The catalog and placement of design C across gru and sjc (docs/terrabase/multi-region-replication.md, section 20):
/// E12 and E13. Each test creates its own databases in the cluster's project and deletes them when it ends.
/// </summary>
public sealed class CatalogE2ETests
{
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail()
    {
        await Gaps.RunAsync("E12", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            await cluster.OwningDatabasesAsync(async created =>
            {
                var name = NewName();
                created.Add((Cluster.Sjc, name));
                _ = await CreateAsync(cluster, Cluster.Sjc, name);
                var path = $"{cluster.ProjectPath}/databases/{name}";
                _ = await Converge.UntilAsync(() => Docs.StatusAsync(cluster[Cluster.Gru], Relative(path)), status => status == HttpStatusCode.OK, "sjc's database on gru");
                _ = await Docs.PostAsync(cluster[Cluster.Gru], Relative($"{path}/collections"), new JsonObject { ["name"] = "items" });
                await Docs.PutAsync(cluster[Cluster.Gru], Relative($"{path}/collections/items/documents/by-key?id={EntityIds.New()}"), new JsonObject { ["from"] = "gru" });

                // Same name in both regions at once: a region that already sees the other's answers 409, which is fine.
                var twin = NewName();
                created.Add((Cluster.Gru, twin));
                created.Add((Cluster.Sjc, twin));
                _ = await Task.WhenAll(CreateAsync(cluster, Cluster.Gru, twin, HttpStatusCode.Conflict), CreateAsync(cluster, Cluster.Sjc, twin, HttpStatusCode.Conflict));
                var twinPath = Relative($"{cluster.ProjectPath}/databases/{twin}");
                _ = await Converge.UntilAsync(
                    async () => (Gru: await IdAsync(cluster[Cluster.Gru], twinPath), Sjc: await IdAsync(cluster[Cluster.Sjc], twinPath)),
                    pair => pair.Gru is not null && pair.Gru == pair.Sjc, "both regions serve one database of the twin name");

            });
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail()
    {
        await Gaps.RunAsync("E13", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var name = NewName();
            var path = $"{cluster.ProjectPath}/databases/{name}";
            await cluster.OwningDatabasesAsync(async created =>
            {
                created.Add((Cluster.Gru, name));
                _ = await CreateAsync(cluster, Cluster.Gru, name);
                _ = await Docs.PostAsync(cluster[Cluster.Gru], Relative($"{path}/collections"), new JsonObject { ["name"] = "items" });
                // Writes keep going through gru; only acknowledged ones must survive the move.
                var acknowledged = new ConcurrentBag<string>();
                using var stop = new CancellationTokenSource();
                var load = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var id = EntityIds.New().ToString();
                        try
                        {
                            await Docs.PutAsync(cluster[Cluster.Gru], Item(path, id), new JsonObject { ["id"] = id });
                            acknowledged.Add(id);
                        }
                        catch (HttpRequestException)
                        {
                        }
                    }
                });
                try
                {
                    var operation = await Docs.PostAsync(cluster[Cluster.Gru], Relative($"{path}/move"), new JsonObject { ["region"] = Cluster.Sjc });
                    var status = Relative($"/v1/operations/{operation?["id"]?.GetValue<string>()}");
                    var done = await Converge.UntilAsync(
                        async () => await Docs.BodyAsync(await cluster[Cluster.Gru].GetAsync(status)),
                        state => state?["state"]?.GetValue<string>() is "succeeded" or "failed", "move operation");
                    Expected.Check(done?["state"]?.GetValue<string>() == "succeeded", $"the move succeeds; {done?.ToJsonString()}");
                }
                finally
                {
                    await stop.CancelAsync();
                    await load;
                }

                Expected.Check(!acknowledged.IsEmpty, "writes were acknowledged during the move");
                foreach (var id in acknowledged)
                {
                    Expected.Check(await Docs.ReadAsync(cluster[Cluster.Sjc], Item(path, id)) is not null, $"acknowledged write {id} is in sjc");
                }

            });
        });
    }

    // A lowercase letter first, at most 63 characters (the database name rule).
    private static string NewName() => $"e2e{Guid.NewGuid():N}";

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static Uri Item(string path, string id) => Relative($"{path}/collections/items/documents/by-key?id={id}");

    // Creates the database in region; an answer in tolerated counts as done.
    private static async Task<HttpStatusCode> CreateAsync(Cluster cluster, string region, string name, HttpStatusCode? tolerated = null)
    {
        using var response = await cluster[region].PostAsJsonAsync(Relative($"{cluster.ProjectPath}/databases"), new { name });
        if (response.StatusCode != tolerated)
        {
            await Docs.EnsureSuccessAsync(response);
        }

        return response.StatusCode;
    }

    private static async Task<string?> IdAsync(HttpClient client, Uri database)
    {
        using var response = await client.GetAsync(database);
        return response.StatusCode == HttpStatusCode.NotFound ? null : (await Docs.BodyAsync(response))?["id"]?.GetValue<string>();
    }
}
