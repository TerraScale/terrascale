using System.Net;
using Bogus;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.E2E.PitrRepro;

/// <summary>
/// Suspected defect 1: <c>BackupReader.SegmentsAsync</c> stops at the first segment read after the restore time before it
/// checks contiguity, so a segment missing from the bucket just before that cut (or at the end of the generation) is
/// skipped silently. Each test removes one segment object, as a lost upload or a bucket-side deletion would.
/// </summary>
[Category("pitr-repro")]
public sealed class SegmentGapRepro
{
    private static readonly Faker Faker = new();

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Restore_between_a_missing_segment_and_the_next_one_succeeds_without_the_missing_writes(CancellationToken cancellationToken)
    {
        ReproHost.SkipUnlessRequested();
        await using var host = await ReproHost.StartAsync();
        using var shipped = await ShipThreeSegmentsAsync(host, cancellationToken);
        var (database, data, segments) = (shipped.Database, shipped.Data, shipped.Segments);
        await host.DeleteObjectAsync(segments[1].Key, cancellationToken);
        ReproHost.Log($"deleted s2 {segments[1].Key}; window {await host.WindowAsync(database, cancellationToken)}");

        // Between s2 (B read) and s3 (C read): B committed 1.5 s before this time.
        var between = segments[1].At + (segments[2].At - segments[1].At) / 2;
        ReproHost.Log($"plan at {between:O}: {await host.PlanAsync(database, between, cancellationToken)}");
        var (status, body) = await host.RestoreAsync(database, between, "gap-between", cancellationToken);
        ReproHost.Log($"restore at {between:O} -> {(int)status} {body}");
        await Assert.That(status).IsEqualTo(HttpStatusCode.Created);
        var seed = await ReproHost.ReadAsync(data, database, "gap-between", "people", "seed", cancellationToken);
        var a = await ReproHost.ReadAsync(data, database, "gap-between", "people", "a", cancellationToken);
        var b = await ReproHost.ReadAsync(data, database, "gap-between", "people", "b", cancellationToken);
        var liveB = await ReproHost.ReadAsync(data, database, database.Name, "people", "b", cancellationToken);
        ReproHost.Log($"restored gap-between: seed={(int)seed} a={(int)a} b={(int)b}; live b={(int)liveB}");
        await Assert.That(seed).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(a).IsEqualTo(HttpStatusCode.OK);
        // The defect: B is silently absent from a restore that reported success.
        await Assert.That(b).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(liveB).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Restore_at_or_after_the_segment_following_a_gap_fails_with_missing_a_log_segment(CancellationToken cancellationToken)
    {
        ReproHost.SkipUnlessRequested();
        await using var host = await ReproHost.StartAsync();
        using var shipped = await ShipThreeSegmentsAsync(host, cancellationToken);
        var (database, data, segments) = (shipped.Database, shipped.Data, shipped.Segments);
        await host.DeleteObjectAsync(segments[1].Key, cancellationToken);

        var (status, body) = await host.RestoreAsync(database, segments[2].At, "gap-after", cancellationToken);
        var direct = await host.RestoreDirectAsync(database, segments[2].At, cancellationToken);
        var created = await ReproHost.ReadAsync(data, database, "gap-after", "people", "seed", cancellationToken);
        ReproHost.Log($"restore at s3 {segments[2].At:O} -> {(int)status} {body}; direct: {direct}; gap-after seed={(int)created}");
        await Assert.That(status).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(direct).IsEqualTo("InvalidDataException: the backup of this database is missing a log segment");
        await Assert.That(created).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Restore_at_latest_succeeds_without_the_writes_of_a_missing_final_segment(CancellationToken cancellationToken)
    {
        ReproHost.SkipUnlessRequested();
        await using var host = await ReproHost.StartAsync();
        using var shipped = await ShipThreeSegmentsAsync(host, cancellationToken);
        var (database, data, segments) = (shipped.Database, shipped.Data, shipped.Segments);
        // s3 is the last segment of the generation: nothing after it can reveal the hole.
        await host.DeleteObjectAsync(segments[2].Key, cancellationToken);

        var latest = await host.LatestAsync(database, cancellationToken);
        ReproHost.Log($"deleted final s3 {segments[2].Key}; window {await host.WindowAsync(database, cancellationToken)}");
        ReproHost.Log($"plan at latest: {await host.PlanAsync(database, latest, cancellationToken)}");
        await Assert.That(latest).IsEqualTo(segments[2].At);
        var (status, body) = await host.RestoreAsync(database, latest, "gap-final", cancellationToken);
        ReproHost.Log($"restore at latest {latest:O} -> {(int)status} {body}");
        await Assert.That(status).IsEqualTo(HttpStatusCode.Created);
        var b = await ReproHost.ReadAsync(data, database, "gap-final", "people", "b", cancellationToken);
        var c = await ReproHost.ReadAsync(data, database, "gap-final", "people", "c", cancellationToken);
        var liveC = await ReproHost.ReadAsync(data, database, database.Name, "people", "c", cancellationToken);
        ReproHost.Log($"restored gap-final: b={(int)b} c={(int)c}; live c={(int)liveC}");
        await Assert.That(b).IsEqualTo(HttpStatusCode.OK);
        // The defect: the window promises C's time, and the restore reports success without C.
        await Assert.That(c).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(liveC).IsEqualTo(HttpStatusCode.OK);
    }

    // seed (in the generation's snapshot), then A, B and C each shipped as their own segment s1, s2, s3, one second apart.
    private static async Task<Shipped> ShipThreeSegmentsAsync(
        ReproHost host, CancellationToken cancellationToken)
    {
        var database = await host.Api.CreateManagedDatabaseAsync("main");
        var data = await host.Api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people", cancellationToken);
        await ReproHost.PutAsync(data, database, "people", "seed", Faker.Lorem.Sentence(8), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        foreach (var name in new[] { "a", "b", "c" })
        {
            await ReproHost.PutAsync(data, database, "people", name, Faker.Lorem.Sentence(8), cancellationToken);
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var segments = host.Segments(database, host.Status(database).Generation!);
        ReproHost.Log($"segments: {string.Join(", ", segments.Select(segment => $"{segment.Key[(segment.Key.LastIndexOf('/') + 1)..]} (at {segment.At:O})"))}");
        await Assert.That(segments.Count).IsEqualTo(3);
        return new(database, data, segments);
    }

    private sealed record Shipped(ManagedDatabaseRef Database, HttpClient Data, IReadOnlyList<SegmentKey> Segments) : IDisposable
    {
        public void Dispose() => Data.Dispose();
    }
}
