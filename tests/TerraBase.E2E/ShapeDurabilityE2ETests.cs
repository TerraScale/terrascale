using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Shapes serve only remotely durable rows (FR-25): a chunk the origin seals over its own commit that no other region
/// acknowledged is never read back, because a SIGKILL of the origin loses that commit. Driven through the real sealer of
/// each region process (<see cref="ShapeSealProbeEndpoint"/>), since the shape endpoint does not serve yet (FR-20).
/// </summary>
public sealed class ShapeDurabilityE2ETests
{
    private const string Origin = Cluster.Gru;
    private const string Remote = Cluster.Sjc;

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task An_unacknowledged_origin_write_is_never_read_from_a_sealed_chunk_and_dies_with_its_origin()
    {
        Skip.When(Cluster.FlyRequested, "kills disposable local region processes");
        await using var regions = await RegionProcesses.StartAsync(Origin, Remote);
        await ConnectedAsync(regions);
        var shape = Shape.New();
        var (kept, overwritten, inserted) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var (keptValue, durableValue, heldValue, insertedValue) =
            (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());

        // Once sjc acknowledged it, the sealer serves the write: the probe reads real chunks, and the frontier moves.
        await PutDurableAsync(regions, kept, keptValue);
        _ = await Converge.UntilAsync(() => SealAsync(regions, shape), seal => Ops(seal).Any(op => op.Id == kept),
            "the acknowledged write in a chunk gru serves");
        await PutDurableAsync(regions, overwritten, durableValue);

        // sjc refuses the database's batches from here on, so gru commits both writes locally and waits for an ack.
        regions.HoldReplication(Remote);
        var overwrite = regions[Origin].PutAsJsonAsync(regions.Document(overwritten), new { value = heldValue });
        var insert = regions[Origin].PutAsJsonAsync(regions.Document(inserted), new { value = insertedValue });
        foreach (var (id, value) in new[] { (overwritten, heldValue), (inserted, insertedValue) })
        {
            _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[Origin], regions.Document(id)),
                document => document?["value"]?.GetValue<string>() == value, $"gru's local commit of {id}");
        }

        var held = await SealAsync(regions, shape);
        var frontier = held.GetProperty("frontier").GetInt64();
        foreach (var chunk in held.GetProperty("chunks").EnumerateArray())
        {
            await Assert.That(Math.Max(chunk.GetProperty("end").GetInt64(), chunk.GetProperty("values_at").GetInt64())).IsLessThanOrEqualTo(frontier)
                .Because("a chunk is read only once its range and values are remotely durable");
        }

        var ops = Ops(held);
        await Assert.That(ops.Any(op => op.Id == kept)).IsTrue().Because("the acknowledged chunk is still served");
        await Assert.That(ops.Any(op => op.Id == inserted)).IsFalse().Because($"no region acknowledged the insert: {held}");
        await Assert.That(ops.Any(op => op.Value == heldValue)).IsFalse().Because($"no chunk carries the unacknowledged overwrite's value: {held}");
        var persisted = Ops(held, "persisted");
        await Assert.That(persisted.Any(op => op.Id == inserted || op.Value == heldValue)).IsFalse()
            .Because("no persisted immutable chunk ever contained either unacknowledged value");

        // The only copy of both writes dies with gru, unanswered as remote durable.
        await regions.KillAsync(Origin);
        foreach (var pending in new[] { overwrite, insert })
        {
            try
            {
                using var response = await pending;
                await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Created).And.IsNotEqualTo(HttpStatusCode.OK)
                    .Because(await response.Content.ReadAsStringAsync());
            }
            catch (HttpRequestException)
            {
                // Killed before it answered.
            }
            catch (OperationCanceledException)
            {
                // KillAsync disposes the killed process's client, cancelling its pending requests.
            }
        }

        // sjc never held them: a chunk that had served them would have published rows no region keeps.
        await Assert.That(await Docs.ReadAsync(regions[Remote], regions.Document(inserted))).IsNull();
        await Assert.That((await Docs.ReadAsync(regions[Remote], regions.Document(overwritten)))?["value"]?.GetValue<string>()).IsEqualTo(durableValue);
    }

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task The_remote_durable_frontier_survives_the_origins_SIGKILL()
    {
        Skip.When(Cluster.FlyRequested, "kills disposable local region processes");
        await using var regions = await RegionProcesses.StartAsync(Origin, Remote);
        await ConnectedAsync(regions);
        var shape = Shape.New();
        var id = EntityIds.New().ToString();
        await PutDurableAsync(regions, id, EntityIds.New().ToString());
        var before = await Converge.UntilAsync(() => SealAsync(regions, shape), seal => Ops(seal).Any(op => op.Id == id),
            "the acknowledged write in a chunk gru serves");

        // sjc acknowledges nothing of the database after the restart, so only gru's persisted frontier can serve the chunk.
        regions.HoldReplication(Remote);
        await regions.KillAsync(Origin);
        await regions.StartAgainAsync(Origin);
        var after = await SealAsync(regions, shape);
        await Assert.That(after.GetProperty("frontier").GetInt64()).IsGreaterThanOrEqualTo(before.GetProperty("frontier").GetInt64());
        await Assert.That(Ops(after).Any(op => op.Id == id)).IsTrue().Because($"the chunk served before the SIGKILL: {after}");
    }

    private static async Task ConnectedAsync(RegionProcesses regions) =>
        _ = await Converge.UntilAsync(() => regions[Origin].GetFromJsonAsync<JsonElement>(new Uri("/replication", UriKind.Relative)),
            status => status.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("region").GetString() == Remote),
            $"{Origin} streams to {Remote}");

    private static async Task PutDurableAsync(RegionProcesses regions, string id, string value)
    {
        using var response = await regions[Origin].PutAsJsonAsync(regions.Document(id), new { value });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
    }

    // Seals the shape on the origin and reads back what its sealer serves.
    private static async Task<JsonElement> SealAsync(RegionProcesses regions, Shape shape)
    {
        var tenant = regions.Tenant;
        var query = string.Join('&', new Dictionary<string, string>
        {
            ["organization"] = tenant.OrganizationId,
            ["project"] = tenant.ProjectId!,
            ["database"] = tenant.DatabaseId!,
            ["collection"] = RegionProcesses.CollectionName,
            ["shape"] = shape.Id,
            ["handle"] = shape.Handle,
        }.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        using var response = await regions[Origin].PostAsync(new Uri($"{ShapeSealProbeEndpoint.ProbeRoute}?{query}", UriKind.Relative), content: null);
        await Docs.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Every op of every served chunk, with the document's value when it carries one.
    private static (string Id, string? Value)[] Ops(JsonElement seal, string field = "chunks") =>
        [.. seal.GetProperty(field).EnumerateArray()
            .SelectMany(chunk => chunk.GetProperty("body").GetProperty("changes").EnumerateArray())
            .SelectMany(change => change.GetProperty("ops").EnumerateArray())
            .Select(op => (op.GetProperty("id").GetString()!,
                op.TryGetProperty("document", out var document) && document.TryGetProperty("value", out var value) ? value.GetString() : null))];

    private sealed record Shape(string Id, string Handle)
    {
        public static Shape New() =>
            new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), "h1-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
    }
}
