using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// V1: a power loss never leaves a sealed shape chunk (or snapshot page) that names a write which did not survive. gru
/// seals chunks remote durable under FR-25 (sjc acknowledges), then loses power at the worst syncs: a write committed
/// after the seals, the seal's own commit, or the acking replica's apply. After restart every op of every chunk the
/// shape HTTP API serves, the sealer serves and the chunk rows persist resolves to the document value read back.
/// </summary>
public sealed class ShapeSealPowerLossTests
{
    private const string Origin = Cluster.Gru;
    private const string Remote = Cluster.Sjc;

    [Test]
    [Arguments("write", 181, 1)]
    [Arguments("write", 802, 2048)]
    [Arguments("seal", 13013, 12)]
    [Arguments("seal", 4242, 3000)]
    [Arguments("acker", 77, 1)]
    [Arguments("acker", 9001, 2048)]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_power_loss_never_leaves_a_sealed_chunk_naming_a_lost_write(string cut, int seed, int tornBytes)
    {
        Skip.When(Cluster.FlyRequested, "kills local region processes and edits their files");
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var regions = await RegionProcesses.StartWithWalFaultsAsync(Origin, Remote);
        await ShapeDurabilityE2ETests.ConnectedAsync(regions);
        var random = new Random(seed);
        var payload = new Faker { Random = new Randomizer(seed) }.Random.String2(random.Next(512, 6000), "abcdefghijklmnopqrstuvwxyz");
        Dictionary<string, string> acknowledged = [with(StringComparer.Ordinal)];
        async Task PutAsync(int count)
        {
            for (var n = 0; n < count; n++)
            {
                var (id, value) = (EntityIds.New().ToString(), $"{acknowledged.Count}-{payload}");
                await ShapeDurabilityE2ETests.PutDurableAsync(regions, id, value);
                acknowledged[id] = value;
            }
        }

        // A snapshot over the first writes, then chunks over the rest, on the HTTP lineage and the sealer's own one.
        await PutAsync(random.Next(3, 7));
        var http = await ReadShapeAsync(regions, null, "-1", acknowledged.Keys);
        await PutAsync(random.Next(3, 7));
        http = await ReadShapeAsync(regions, http.Handle, http.Next, acknowledged.Keys, http.Ops);
        var shape = ShapeDurabilityE2ETests.Shape.New();
        var before = await Converge.UntilAsync(() => ShapeDurabilityE2ETests.SealAsync(regions, shape),
            seal => acknowledged.Keys.All(id => ShapeDurabilityE2ETests.Ops(seal).Any(op => op.Id == id)), "every acknowledged write in a served chunk");
        var cutRegion = cut == "acker" ? Remote : Origin;
        var interrupted = EntityIds.New().ToString();
        Task pending;
        if (cut == "seal")
        {
            await PutAsync(1);
            regions.Faults(Origin).KillAtSync(1, regions.TenantWal(Origin));
            pending = ShapeDurabilityE2ETests.SealAsync(regions, shape);
        }
        else
        {
            regions.Faults(cutRegion).KillAtSync(1, regions.TenantWal(cutRegion));
            pending = regions[Origin].PutAsJsonAsync(regions.Document(interrupted), new { value = $"interrupted-{payload}" });
        }

        if (cutRegion == Remote)
        {
            // sjc restarts on what survived alone: nothing of the database is applied again until it was read back.
            regions.HoldReplication(Remote);
        }

        var (synced, unsynced, retained) = await regions.PowerLossAtSyncAsync(cutRegion, tornBytes);
        await regions.StartAgainAsync(cutRegion);
        if (cutRegion == Origin)
        {
            await IgnoreAsync(pending);
        }

        await Assert.That(unsynced).IsGreaterThan(0).Because("the cut sync left frames written but not synced");
        List<string> lost = [];
        Dictionary<(string Region, string Id), string?> resolved = [];
        async Task CheckAsync(string region, IEnumerable<(string Id, string? Value)> ops, string where)
        {
            foreach (var (id, value) in ops)
            {
                if (!resolved.TryGetValue((region, id), out var read))
                {
                    read = (await Docs.ReadAsync(regions[region], regions.Document(id)))?["value"]?.GetValue<string>();
                    resolved[(region, id)] = read;
                }

                if (read != value)
                {
                    lost.Add($"{where} on {region}: {id} is {read ?? "missing"}, not {value?[..Math.Min(value.Length, 12)]}");
                }
            }
        }

        _ = await Converge.UntilAsync(() => Docs.StatusAsync(regions[cutRegion], new Uri($"{regions.DatabasePath}/collections", UriKind.Relative)),
            status => status == HttpStatusCode.OK, $"the database on {cutRegion} after the power loss");
        var opsChecked = 0;
        if (cutRegion == Remote)
        {
            // Every write in a chunk gru served was acknowledged by sjc, so sjc kept it through its own power loss.
            var served = ShapeDurabilityE2ETests.Ops(before).Concat(http.Ops).ToArray();
            await CheckAsync(Remote, served, "pre-loss chunk");
            opsChecked += served.Length;
            regions.ReleaseReplication(Remote);
        }
        else
        {
            await ShapeDurabilityE2ETests.ConnectedAsync(regions);
        }

        var recovered = 0;
        foreach (var (id, value) in acknowledged)
        {
            recovered += (await Docs.ReadAsync(regions[Origin], regions.Document(id)))?["value"]?.GetValue<string>() == value ? 1 : 0;
        }

        // The old lineage resumes where it was or rotates; never a 200 naming a lost write. Then a fresh bootstrap.
        var resume = await ReadShapeAsync(regions, http.Handle, http.Next, [], refetch: true);
        var fresh = await ReadShapeAsync(regions, null, "-1", acknowledged.Keys);
        var after = await Converge.UntilAsync(() => ShapeDurabilityE2ETests.SealAsync(regions, shape),
            seal => acknowledged.Keys.All(id => ShapeDurabilityE2ETests.Ops(seal).Any(op => op.Id == id)), "every acknowledged write served again");
        var chunks = after.GetProperty("chunks").GetArrayLength() + after.GetProperty("persisted").GetArrayLength() + resume.Bodies + fresh.Bodies;
        (string Id, string? Value)[] origin = [.. ShapeDurabilityE2ETests.Ops(after), .. ShapeDurabilityE2ETests.Ops(after, "persisted"),
            .. resume.Ops, .. fresh.Ops, .. ShapeDurabilityE2ETests.Ops(before), .. http.Ops];
        await CheckAsync(Origin, origin, "chunk");
        opsChecked += origin.Length;
        var servedAfter = ShapeDurabilityE2ETests.Ops(after).Concat(fresh.Ops).ToArray();
        await CheckAsync(Remote, servedAfter, "served chunk");
        opsChecked += servedAfter.Length;

        // A chunk served before the power loss is immutable: the same range and body after it.
        var (old, now) = (before.GetProperty("chunks").EnumerateArray().ToArray(), after.GetProperty("chunks").EnumerateArray().ToArray());
        var changed = old.Index().Count(chunk => chunk.Index >= now.Length || chunk.Item.GetRawText() != now[chunk.Index].GetRawText());
        var kept = await Docs.ReadAsync(regions[Origin], regions.Document(interrupted)) is null ? 0 : 1;
        var evidence = string.Create(CultureInfo.InvariantCulture,
            $"V1 cut={cut} seed={seed} acknowledged={acknowledged.Count} recovered={recovered} chunks_checked={chunks} ops_checked={opsChecked} lost_in_chunk={lost.Count} changed_chunks={changed} synced_bytes={synced} unsynced_bytes={unsynced} retained_bytes={retained} torn_bytes={tornBytes} http_resume={(resume.Refetched ? "409" : "200")} interrupted_kept={kept}");
        Console.WriteLine(evidence);
        Outcomes.Mark(lost.Count == 0 && changed == 0 && recovered == acknowledged.Count ? "pass" : "fail", evidence);
        await Assert.That(recovered).IsEqualTo(acknowledged.Count).Because("every remote durable write survives");
        await Assert.That(lost).IsEmpty().Because($"no chunk names a write lost in the power loss: {string.Join("; ", lost.Take(5))}");
        await Assert.That(changed).IsEqualTo(0).Because("a served chunk keeps its range and body");
        if (cutRegion == Remote)
        {
            await IgnoreAsync(pending);
        }
    }

    // The interrupted request: its process died, or it answered once sjc was back; either way it settles nothing here.
    private static async Task IgnoreAsync(Task pending)
    {
        try
        {
            await pending;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // Killed before it answered.
        }
    }

    // Reads the shape on gru from offset (-1 bootstraps) to the sealed head until every wanted id was in a body, retrying
    // 429 and 503 as a client does. With refetch, a 409 must_refetch ends the read instead of failing it.
    private static async Task<ShapeRead> ReadShapeAsync(RegionProcesses regions, string? handle, string offset, IEnumerable<string> wanted,
        List<(string Id, string? Value)>? ops = null, bool refetch = false)
    {
        var shape = $"{regions.DatabasePath}/collections/{RegionProcesses.CollectionName}/shape";
        var read = new ShapeRead(handle ?? "", offset, ops ?? [], 0, false);
        HashSet<string> missing = [with(StringComparer.Ordinal), .. wanted];
        var deadline = DateTime.UtcNow + Converge.Deadline;
        var target = new Uri(offset == "-1" ? $"{shape}?offset=-1" : Continue(), UriKind.Relative);
        string Continue() => $"{shape}?handle={Uri.EscapeDataString(read.Handle)}&offset={Uri.EscapeDataString(read.Next)}";
        while (true)
        {
            using var response = await regions[Origin].GetAsync(target);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Conflict && refetch)
            {
                return read with { Refetched = true };
            }

            if (response.StatusCode == HttpStatusCode.TemporaryRedirect)
            {
                read = read with { Handle = response.Headers.GetValues("TB-Shape-Handle").Single() };
                target = response.Headers.Location!;
                continue;
            }

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            foreach (var op in root.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray()))
            {
                var id = op.GetProperty("id").GetString()!;
                read.Ops.Add((id, op.TryGetProperty("document", out var document) && document.TryGetProperty("value", out var value) ? value.GetString() : null));
                _ = missing.Remove(id);
            }

            read = read with { Handle = root.GetProperty("handle").GetString()!, Next = root.GetProperty("next_offset").GetString()!, Bodies = read.Bodies + 1 };
            if (!read.Next.StartsWith("snap.", StringComparison.Ordinal) && root.GetProperty("up_to_date").GetBoolean())
            {
                if (missing.Count == 0)
                {
                    return read;
                }

                await Assert.That(DateTime.UtcNow).IsLessThan(deadline).Because($"the shape served {string.Join(", ", missing)} by now");
                await Task.Delay(250);
            }

            target = new Uri(Continue(), UriKind.Relative);
        }
    }

    private sealed record ShapeRead(string Handle, string Next, List<(string Id, string? Value)> Ops, int Bodies, bool Refetched);
}
