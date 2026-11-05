using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Sync;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeOverlayTests
{
    [Test]
    public async Task A_local_write_reaches_the_sjc_tail_as_provisional_at_once_and_the_home_pages_confirm_it()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await HomePushTests.World.StartAsync(nats);
        var (tails, shape, offset) = await TailAsync(world, token);

        var parked = tails.WaitAsync(shape.Scope, shape.Lineage, offset, ShapeProvisional.Empty, token).AsTask();
        var committed = await world.LocalAsync(homeDurable: false, ["INSERT INTO items (id, v) VALUES ('p', 'sjc')"], token);
        var provisional = Body(await parked.WaitAsync(TimeSpan.FromSeconds(5), token));
        await Assert.That(provisional.GetProperty("next_offset").GetString()).IsEqualTo(Decimal(offset));
        var pending = provisional.GetProperty("provisional").EnumerateArray().Single();
        await Assert.That(pending.GetProperty("id").GetString()).IsEqualTo(committed.Id);
        var op = pending.GetProperty("ops").EnumerateArray().Single();
        await Assert.That(op.GetProperty("op").GetString()).IsEqualTo("insert");
        await Assert.That(op.GetProperty("key").GetString()!).StartsWith("~" + committed.Id + ".");
        await Assert.That(op.GetProperty("row").GetProperty("v").GetString()).IsEqualTo("sjc");
        var local = provisional.GetProperty("local").GetString()!;

        await using (var home = await world.HomeNodeAsync())
        {
            await TerraBaseTestServer.EventuallyAsync(async _ => (await world.HomeSqlAsync("SELECT count(*) FROM items")).GetRawText() == "[[1]]", TimeSpan.FromSeconds(30));
        }

        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);
        var confirmed = Body(await tails.WaitAsync(shape.Scope, shape.Lineage, offset, local, token));
        var commit = confirmed.GetProperty("changes").EnumerateArray().Single();
        await Assert.That(commit.GetProperty("confirmed").EnumerateArray().Single().GetString()).IsEqualTo(committed.Id);
        await Assert.That(commit.GetProperty("ops").EnumerateArray().Single().GetProperty("key").GetString()!).DoesNotStartWith("~");
        await Assert.That(confirmed.GetProperty("provisional").GetArrayLength()).IsEqualTo(0);
        await Assert.That(confirmed.GetProperty("local").GetString()).IsEqualTo(ShapeProvisional.Empty);
    }

    [Test]
    public async Task A_local_write_the_home_rejects_turns_into_a_revert_on_the_sjc_tail()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var world = await HomePushTests.World.StartAsync(nats);
        var (tails, shape, offset) = await TailAsync(world, token);
        await world.HomeSqlAsync("INSERT INTO items (id, v) VALUES ('x', 'gru')");

        var committed = await world.LocalAsync(homeDurable: false, ["INSERT INTO items (id, v) VALUES ('x', 'sjc')"], token);
        var provisional = Body(await tails.WaitAsync(shape.Scope, shape.Lineage, offset, ShapeProvisional.Empty, token));
        await Assert.That(provisional.GetProperty("provisional").EnumerateArray().Single().GetProperty("id").GetString()).IsEqualTo(committed.Id);

        await using (var home = await world.HomeNodeAsync())
        {
            await TerraBaseTestServer.EventuallyAsync(async _ => (await world.HomeSqlAsync("SELECT count(*) FROM terrascale.conflicts")).GetRawText() == "[[1]]",
                TimeSpan.FromSeconds(30));
        }

        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, token);
        var reverted = Body(await tails.WaitAsync(shape.Scope, shape.Lineage, offset, provisional.GetProperty("local").GetString(), token));
        var changes = reverted.GetProperty("changes").EnumerateArray().ToList();
        await Assert.That(changes.SelectMany(change => change.TryGetProperty("revert", out var ids) ? ids.EnumerateArray().Select(id => id.GetString()) : []).ToList())
            .IsEquivalentTo([committed.Id]);
        await Assert.That(changes.SelectMany(change => change.GetProperty("ops").EnumerateArray()).Single().GetProperty("row").GetProperty("v").GetString()).IsEqualTo("gru");
        await Assert.That(reverted.GetProperty("provisional").GetArrayLength()).IsEqualTo(0);
    }

    private static async Task<(ReplicaShapeTails Tails, ResolvedShape Shape, long Offset)> TailAsync(HomePushTests.World world, CancellationToken cancellationToken)
    {
        // Opens the replica's writer before a tail polls: the first sync connect of a writer is refused while another connects.
        _ = await world.LocalAsync(homeDurable: false, [], cancellationToken);
        var home = new TableShapeClient(world.Writer, ManagementApi.Uri(world.Reference.Data).ToString(), "items");
        _ = await home.SyncAsync();
        _ = await world.Replicas.PullAsync(world.Database, fromHome: true, cancellationToken);
        var services = world.Api.Server.Services;
        var tails = new ReplicaShapeTails(world.Replicas, services.GetRequiredService<IMemoryBudget>(), TimeProvider.System,
            services.GetRequiredService<IShapeFlights<ShapeRead>>(), services.GetRequiredService<ShapeControls>(), world.Overlay);
        var resolved = await tails.ResolveAsync(world.Database, "items", new ShapeQuery(null, null), cancellationToken);
        var shape = resolved is ResolvedShape found ? found : throw new InvalidOperationException("the sjc replica did not resolve the shape");
        var offset = long.Parse(home.NextOffset, System.Globalization.CultureInfo.InvariantCulture);
        await using var reader = (await world.Replicas.BeginShapeReadAsync(world.Database, cancellationToken))!;
        while (await reader.ReadAsync(shape.Scope, shape.Lineage, offset, services.GetRequiredService<IMemoryBudget>(), cancellationToken) is ShapeBody body)
        {
            body.Lease?.Dispose();
            offset = long.Parse(body.NextOffset, System.Globalization.CultureInfo.InvariantCulture);
            if (body.UpToDate)
            {
                break;
            }
        }

        return (tails, shape, offset);
    }

    private static JsonElement Body(ShapeRead read)
    {
        var body = read is ShapeBody answered ? answered : throw new InvalidOperationException("the tail answered " + read switch
        {
            MustRefetch refetch => "must_refetch: " + refetch.Message,
            Unavailable unavailable => "unavailable: " + unavailable.Message,
            QuotaExceeded busy => "busy: " + busy.Message,
            _ => "not found",
        });
        using (body.Lease)
        {
            return JsonElement.Parse(body.Json.Span);
        }
    }

    private static string Decimal(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
