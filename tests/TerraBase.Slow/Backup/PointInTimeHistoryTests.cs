using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

/// <summary>
/// A point-in-time restore succeeds only when the bucket proves the database's history complete up to the requested time;
/// otherwise it answers 409 <c>backup_history_incomplete</c> and creates nothing. Each test removes what a lost upload, a
/// bucket-side deletion or an ended generation removes, then checks which times still restore, and with exactly what.
/// </summary>
public sealed class PointInTimeHistoryTests
{
    private const string Code = "backup_history_incomplete";
    private const string Message = "continuous backup cannot prove this database's history complete up to pointInTime";
    private const string Unproven = "restore_point_unproven";
    private const string Unreadable = "backup_unreadable";
    private const string UnreadableMessage = "continuous backup of this database cannot be read: an object fails authentication or its integrity check";
    private const string Unplaced = "continuous backup cannot place pointInTime between two transactions: ";
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_missing_middle_segment_refuses_every_time_after_the_segment_before_it_and_restores_exactly_up_to_it(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var (s1, s2, s3) = await host.ShipThreeSegmentsAsync(cancellationToken);
        await host.DeleteAsync(s2.Key, cancellationToken);
        var catalog = await host.CatalogAsync(cancellationToken);

        // s2's own time is gone with it: from just after s1 on, nothing proves where the history continues.
        foreach (var at in new[] { s1.At.AddMilliseconds(500), s2.At, s2.At.AddMilliseconds(500), s3.At })
        {
            await host.RefusedAsync(at, cancellationToken);
        }

        // Object names cannot show a missing middle segment (only its length can); the plan says it checked names only.
        var plan = await host.PlanAsync(s2.At.AddMilliseconds(500), cancellationToken);
        await Assert.That(plan.GetProperty("historyCheck").GetString()).IsEqualTo("metadata");
        await Assert.That(await host.CatalogAsync(cancellationToken)).IsEqualTo(catalog);

        await host.RestoresAsync(host.SnapshotAt.AddMilliseconds(500), "before-a", ["seed"], ["a", "b", "c"], cancellationToken);
        await host.RestoresAsync(s1.At, "at-a", ["seed", "a"], ["b", "c"], cancellationToken);
    }

    [Test]
    public async Task A_missing_final_segment_refuses_its_time_in_restore_and_plan_and_restores_exactly_before_it(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var (_, s2, s3) = await host.ShipThreeSegmentsAsync(cancellationToken);
        // Nothing follows the generation's last segment to reveal it is gone.
        await host.DeleteAsync(s3.Key, cancellationToken);
        var catalog = await host.CatalogAsync(cancellationToken);

        foreach (var at in new[] { s2.At.AddMilliseconds(500), s3.At })
        {
            await ErrorAssert.ErrorAsync(host.Api.Client.PostAsJsonAsync(ManagementApi.Uri(host.Database.Path + "/point-in-time/plan"), new { pointInTime = at }, cancellationToken),
                HttpStatusCode.Conflict, Code, Message);
            await host.RefusedAsync(at, cancellationToken);
        }

        await Assert.That(await host.CatalogAsync(cancellationToken)).IsEqualTo(catalog);
        await host.RestoresAsync(s2.At, "at-b", ["seed", "a", "b"], ["c"], cancellationToken);
    }

    [Test]
    public async Task A_time_between_an_ended_generation_and_the_next_snapshot_is_refused(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        await host.PutAsync("a", cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var first = host.Generation;
        var s1 = host.Segments(first).Single();

        // b commits, then something empties the capture table before the shipper reads it: the generation ends without b.
        host.Time.Advance(TimeSpan.FromMilliseconds(500));
        await host.PutAsync("b", cancellationToken);
        var tenant = new TenantContext(host.Database.Organization, PlanTier.Pro, host.Database.Project, host.Database.Id);
        var store = (TursoDatabaseStore)(await host.Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, cancellationToken))!;
        await store.WithWriterAsync(writer =>
        {
            using var delete = writer.Connection.CreateCommand();
            delete.CommandText = "DELETE FROM turso_cdc;";
            return delete.ExecuteNonQuery();
        }, "a test", cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var second = host.Time.GetUtcNow();
        await Assert.That(host.Generation).IsNotEqualTo(first);

        // b committed before this time, but only the next generation's snapshot holds it.
        var between = s1.At.AddSeconds(1);
        await ErrorAssert.ErrorAsync(host.Api.Client.PostAsJsonAsync(ManagementApi.Uri(host.Database.Path + "/point-in-time/plan"), new { pointInTime = between }, cancellationToken),
            HttpStatusCode.Conflict, Code, Message);
        await host.RefusedAsync(between, cancellationToken);

        await host.RestoresAsync(s1.At, "at-a", ["seed", "a"], ["b"], cancellationToken);
        await host.RestoresAsync(second, "next-generation", ["seed", "a", "b"], [], cancellationToken);
    }

    [Test]
    public async Task A_quiet_time_up_to_a_later_snapshot_of_the_generation_restores_unless_a_segment_before_it_is_missing(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        await host.PutAsync("a", cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();
        // A day later the generation's daily snapshot holds exactly the log s1 ends at: nothing was read in between.
        await host.ShipAsync(TimeSpan.FromDays(1), cancellationToken);
        var quiet = s1.At.AddHours(1);

        var plan = await host.PlanAsync(quiet, cancellationToken);
        await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(host.SnapshotAt);
        await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(1);
        await host.RestoresAsync(quiet, "quiet", ["seed", "a"], [], cancellationToken);

        // Without s1, the daily snapshot's position shows log that no stored segment holds.
        await host.DeleteAsync(s1.Key, cancellationToken);
        foreach (var at in new[] { s1.At, quiet })
        {
            await ErrorAssert.ErrorAsync(host.Api.Client.PostAsJsonAsync(ManagementApi.Uri(host.Database.Path + "/point-in-time/plan"), new { pointInTime = at }, cancellationToken),
                HttpStatusCode.Conflict, Code, Message);
            await host.RefusedAsync(at, cancellationToken);
        }
    }

    [Test]
    public async Task Empty_segments_sharing_a_start_continue_the_history_in_restore_and_plan(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        await host.PutAsync("a", cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();
        // A segment holding no log ends where it starts: three read before s1, all starting where s1 does.
        foreach (var ms in new[] { 100, 200, 300 })
        {
            await host.StoreAsync(host.SegmentKey(s1.Start, host.SnapshotAt.AddMilliseconds(ms)), [], cancellationToken);
        }

        var last = host.SnapshotAt.AddMilliseconds(300);
        var plan = await host.PlanAsync(last, cancellationToken);
        await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(3);
        await host.RestoresAsync(last, "at-empty", ["seed"], ["a"], cancellationToken);
        // s1, read after the time, starts where the empty ones end.
        await host.RestoresAsync(last.AddMilliseconds(200), "before-a", ["seed"], ["a"], cancellationToken);

        // The daily snapshot holds exactly the log an empty segment after s1 ends at.
        await host.ShipAsync(TimeSpan.FromDays(1), cancellationToken);
        await host.StoreAsync(host.SegmentKey(host.LogBytes, s1.At.AddHours(1)), [], cancellationToken);
        await host.RestoresAsync(s1.At.AddHours(2), "quiet", ["seed", "a"], [], cancellationToken);
    }

    [Test]
    public async Task A_segment_starting_where_a_non_empty_one_starts_is_refused_at_restore(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var (_, s2, s3) = await host.ShipThreeSegmentsAsync(cancellationToken);
        var duplicate = s2.At.AddMilliseconds(500);
        await host.StoreAsync(host.SegmentKey(s2.Start, duplicate), [], cancellationToken);

        // Only s2's length shows it holds log the duplicate does not continue; the plan says it checked names only.
        var plan = await host.PlanAsync(duplicate, cancellationToken);
        await Assert.That(plan.GetProperty("historyCheck").GetString()).IsEqualTo("metadata");
        foreach (var at in new[] { duplicate, s3.At })
        {
            await host.RefusedAsync(at, cancellationToken);
        }

        await host.RestoresAsync(s2.At, "at-b", ["seed", "a", "b"], ["c"], cancellationToken);
    }

    // Turso stamps each captured row with the machine's clock in whole seconds; these tests set the seconds it recorded (the
    // test clock drives segment, snapshot and restore times). k is the second a restore at a whole second starts.
    [Test]
    public async Task A_write_committed_before_the_time_and_read_after_it_restores_and_one_committed_at_it_does_not(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(1);
        await host.PutAsync("a", cancellationToken);
        await host.StampAsync(k.AddSeconds(-1), cancellationToken);
        await host.PutAsync("b", cancellationToken);
        await host.StampAsync(k, cancellationToken);
        // One read after k holds both: a committed in the second before k, b in k's own second.
        await host.ShipAsync(TimeSpan.FromMilliseconds(1500), cancellationToken);

        await host.RestoresAsync(k, "at-k", ["seed", "a"], ["b"], cancellationToken);
    }

    [Test]
    public async Task A_backlog_read_across_the_time_restores_every_transaction_committed_before_it(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(2);
        await host.PutAsync("a", cancellationToken);
        await host.StampAsync(k.AddSeconds(-2), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(3), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();
        // A backlog read that started half a second before k and ended after it: b committed before k, after that read began.
        var log = await host.LogAsync(s1.Key, cancellationToken);
        await Assert.That(log[^9]).IsEqualTo((byte)7);
        await Assert.That(BinaryPrimitives.ReadInt64BigEndian(log.AsSpan(^8))).IsEqualTo(s1.At.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64BigEndian(log.AsSpan(^8), k.AddMilliseconds(-500).ToUnixTimeMilliseconds());
        await host.StoreAsync(s1.Key, log, cancellationToken);
        await host.PutAsync("b", cancellationToken);
        await host.StampAsync(k.AddSeconds(-1), cancellationToken);
        await host.PutAsync("c", cancellationToken);
        await host.StampAsync(k, cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);

        await host.RestoresAsync(k, "at-k", ["seed", "a", "b"], ["c"], cancellationToken);
    }

    [Test]
    public async Task A_whole_second_places_the_time_between_transactions_and_a_time_within_a_commit_second_is_refused(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(2);
        // Three commits in the second before k, one in k's own second.
        foreach (var name in new[] { "a", "b", "c" })
        {
            await host.PutAsync(name, cancellationToken);
            await host.StampAsync(k.AddSeconds(-1), cancellationToken);
        }

        await host.PutAsync("d", cancellationToken);
        await host.StampAsync(k, cancellationToken);
        await host.ShipAsync(TimeSpan.FromMilliseconds(2500), cancellationToken);

        await host.RestoresAsync(k, "at-k", ["seed", "a", "b", "c"], ["d"], cancellationToken);
        await host.RefusedAsync(k.AddMilliseconds(-1), Unproven, Unplaced + Within(k.AddSeconds(-1)), cancellationToken);
        await host.RefusedAsync(k.AddMilliseconds(1), Unproven, Unplaced + Within(k), cancellationToken);
    }

    [Test]
    public async Task A_transaction_is_restored_whole_on_the_side_of_the_time_its_commit_row_is(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(3);
        // Writes grouped in one transaction share its commit, like a group commit's. The first group ran wholly before k; the
        // second started before k (a request sent before it) and committed after it.
        await host.TransactAsync(["x", "y"], cancellationToken);
        await host.StampAsync(k.AddSeconds(-3), k.AddSeconds(-1), cancellationToken);
        await host.TransactAsync(["z", "w"], cancellationToken);
        await host.StampAsync(k.AddSeconds(-1), k.AddSeconds(1), cancellationToken);
        await host.ShipAsync(TimeSpan.FromMilliseconds(4500), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();

        await host.RestoresAsync(k, "at-k", ["seed", "x", "y"], ["z", "w"], cancellationToken);
        await host.RestoresAsync(s1.At, "at-read", ["seed", "x", "y", "z", "w"], [], cancellationToken);
    }

    [Test]
    public async Task Times_that_go_back_are_refused_where_a_restore_needs_them(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(3);
        await host.PutAsync("a", cancellationToken);
        await host.StampAsync(k, cancellationToken);
        // The clock went back two seconds before b.
        await host.PutAsync("b", cancellationToken);
        await host.StampAsync(k.AddSeconds(-2), cancellationToken);
        await host.ShipAsync(TimeSpan.FromMilliseconds(4500), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();
        await host.RefusedAsync(k.AddSeconds(1), Unproven, Unplaced + "the database's clock went back between two of its transactions", cancellationToken);

        // Within c, its first row is stamped after its commit row: the tap records c with no time.
        await host.PutAsync("c", cancellationToken);
        await host.StampAsync(k.AddSeconds(3), k.AddSeconds(2), cancellationToken);
        await host.PutAsync("d", cancellationToken);
        await host.StampAsync(k.AddSeconds(3), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(2), cancellationToken);
        await host.RefusedAsync(s1.At.AddMilliseconds(500), Unproven, Unplaced + NoTime, cancellationToken);
    }

    [Test]
    public async Task A_segment_without_transaction_times_is_refused_where_a_restore_needs_it(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(2);
        // A log written before transaction times were recorded logs a's commit as the tap does for a row with no time.
        await host.PutAsync("a", cancellationToken);
        await host.StampAsync(null, cancellationToken);
        await host.PutAsync("b", cancellationToken);
        await host.StampAsync(k, cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(3), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();

        await host.RefusedAsync(k, Unproven, Unplaced + NoTime, cancellationToken);
        // Its own time still restores: a read mark at its end proves everything committed before then is in it.
        await host.RestoresAsync(s1.At, "at-read", ["seed", "a", "b"], [], cancellationToken);
    }

    [Test]
    public async Task The_latest_point_a_read_mark_proves_restores_exactly_with_no_later_write(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(3);
        await host.PutAsync("a", cancellationToken);
        await host.StampAsync(k.AddSeconds(-2), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(3), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();
        await Assert.That(s1.At).IsEqualTo(k);
        // A read takes time: this one started 300 ms before it ended.
        var log = await host.LogAsync(s1.Key, cancellationToken);
        var started = k.AddMilliseconds(-300);
        BinaryPrimitives.WriteInt64BigEndian(log.AsSpan(^8), started.ToUnixTimeMilliseconds());
        await host.StoreAsync(s1.Key, log, cancellationToken);

        // The whole second at or before the read's start, and any commit-free time up to that start.
        await host.RestoresAsync(k.AddSeconds(-1), "latest", ["seed", "a"], [], cancellationToken);
        await host.RestoresAsync(started, "read-start", ["seed", "a"], [], cancellationToken);
        // Within a's commit second; and after the read started, where a commit acknowledged before s1's time could be unshipped.
        await host.RefusedAsync(k.AddMilliseconds(-1500), Unproven, Unplaced + Within(k.AddSeconds(-2)), cancellationToken);
        foreach (var at in new[] { started.AddMilliseconds(1), k })
        {
            await host.RefusedAsync(at, cancellationToken);
        }
    }

    [Test]
    public async Task An_idle_database_ships_nothing_and_its_last_segment_ends_with_a_read_mark(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        await host.PutAsync("a", cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();
        var log = await host.LogAsync(s1.Key, cancellationToken);
        await Assert.That(log[^9]).IsEqualTo((byte)7);
        await Assert.That(BinaryPrimitives.ReadInt64BigEndian(log.AsSpan(^8))).IsEqualTo(s1.At.ToUnixTimeMilliseconds());

        var puts = host.ObservePuts();
        for (var pass = 0; pass < 5; pass++)
        {
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }

        await Assert.That(puts).IsEmpty();
        await host.RestoresAsync(s1.At, "at-a", ["seed", "a"], [], cancellationToken);
    }

    [Test]
    [Arguments("wrong-master-key")]
    [Arguments("corrupted-segment")]
    [Arguments("truncated-segment")]
    [Arguments("corrupted-snapshot")]
    public async Task An_unreadable_backup_object_is_refused_and_creates_nothing(string fault, CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var (_, s2, s3) = await host.ShipThreeSegmentsAsync(cancellationToken);
        var snapshot = host.SnapshotKey(host.SnapshotAt, 0);
        // Two idle days let the node's key caches lapse, so a restore unwraps the data key from the bucket.
        host.Time.Advance(TimeSpan.FromDays(2));
        var (key, at, change) = fault switch
        {
            // The node's master key did not wrap the data key.
            "wrong-master-key" => (host.DataKeyObject(snapshot), s3.At, (Func<byte[], byte[]>)(original => Encoding.ASCII.GetBytes(
                new MasterKey(RandomNumberGenerator.GetBytes(32)).Wrap(host.Unwrap(Encoding.ASCII.GetString(original)))))),
            "corrupted-segment" => (s2.Key, s3.At, Flip),
            "truncated-segment" => (s2.Key, s3.At, original => original[..^20]),
            _ => (snapshot, host.SnapshotAt, Flip),
        };

        await host.ReplaceAsync(key, change, () => host.RefusedAsync(at, Unreadable, UnreadableMessage, cancellationToken));
        await host.RestoresAsync(at, "intact", at == s3.At ? ["seed", "a", "b", "c"] : ["seed"], [], cancellationToken);

        static byte[] Flip(byte[] original)
        {
            var corrupted = original.ToArray();
            corrupted[^20] ^= 0xff;
            return corrupted;
        }
    }

    [Test]
    public async Task A_copy_of_a_segment_under_a_later_key_is_refused_as_an_overlap_after_its_original(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var (_, s2, s3) = await host.ShipThreeSegmentsAsync(cancellationToken);
        // The same sealed bytes under a key with s2's start, a millisecond later: they are bound to s2's key and do not
        // authenticate under this one, and they overlap s2.
        host.StoreRaw(host.SegmentKey(s2.Start, s2.At.AddMilliseconds(1)), host.Raw(s2.Key));

        await host.RefusedAsync(s3.At, cancellationToken);
        await host.RestoresAsync(s2.At, "at-b", ["seed", "a", "b"], ["c"], cancellationToken);
    }

    [Test]
    public async Task A_time_the_backup_does_not_hold_yet_or_any_more_is_refused_and_only_future_or_expired_times_are_invalid(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        await host.PutAsync("a", cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var s1 = host.Segments(host.Generation).Single();
        // b is committed, its segment not shipped yet.
        await host.PutAsync("b", cancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        // The plan answers each time as the restore does.
        await host.RefusedAsync(s1.At.AddMilliseconds(500), cancellationToken);
        await host.PlanRefusedAsync(s1.At.AddMilliseconds(500), HttpStatusCode.Conflict, Code, Message, cancellationToken);
        var outside = string.Create(CultureInfo.InvariantCulture,
            $"pointInTime must lie between the restore window's earliest and latest times: {host.SnapshotAt:O} to {s1.At:O}");
        foreach (var at in new[] { host.Time.GetUtcNow().AddSeconds(1), host.Time.GetUtcNow().AddYears(-1) })
        {
            await host.RefusedAsync(at, HttpStatusCode.BadRequest, "invalid_request", outside, cancellationToken);
            await host.PlanRefusedAsync(at, HttpStatusCode.BadRequest, "invalid_request", outside, cancellationToken);
        }

        // The daily snapshot ships; then the generation's first snapshot is deleted: s1's time is no longer held.
        await host.ShipAsync(TimeSpan.FromDays(1), cancellationToken);
        await host.DeleteAsync(host.SnapshotKey(host.SnapshotAt, 0), cancellationToken);
        await host.RefusedAsync(s1.At, cancellationToken);
        await host.PlanRefusedAsync(s1.At, HttpStatusCode.Conflict, Code, Message, cancellationToken);
    }

    [Test]
    public async Task A_snapshot_whose_read_transaction_cannot_end_leaves_the_tap_reading_new_commits(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var directory = Directory.CreateTempSubdirectory("terrabase-tap-snapshot-");
        try
        {
            var engine = host.Api.Server.Services.GetRequiredService<TursoStorageEngine>();
            await using var tap = (TursoChangeTap)(await engine.OpenTapAsync(host.Tenant, cancellationToken))!;
            // The snapshot's rollback fails, leaving its read transaction open.
            tap.BeforeSnapshotEnd = _ => throw new IOException("the snapshot's rollback failed");
            await Assert.That(async () => await tap.SnapshotAsync(Stream.Null, Path.Combine(directory.FullName, "snapshot.db"), cancellationToken))
                .Throws<IOException>();

            // A commit after it is in the tap's next read, which ends with a read mark: a pinned reader would see nothing.
            await host.PutAsync("a", cancellationToken);
            using var log = new MemoryStream();
            var read = await tap.ReadAsync(log, cancellationToken);
            await Assert.That(read.Bytes).IsGreaterThan(9);
            await Assert.That(log.ToArray()[^9]).IsEqualTo((byte)7);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task A_snapshot_that_fails_before_pinning_its_read_transaction_leaves_the_tap_reading_new_commits(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var directory = Directory.CreateTempSubdirectory("terrabase-tap-snapshot-");
        try
        {
            var engine = host.Api.Server.Services.GetRequiredService<TursoStorageEngine>();
            await using var tap = (TursoChangeTap)(await engine.OpenTapAsync(host.Tenant, cancellationToken))!;
            // The read that would pin the snapshot's transaction fails after BEGIN, leaving the transaction open.
            tap.AfterSnapshotBegin = _ => throw new IOException("the snapshot's pinning read failed");
            await Assert.That(async () => await tap.SnapshotAsync(Stream.Null, Path.Combine(directory.FullName, "snapshot.db"), cancellationToken))
                .Throws<IOException>();

            // An open transaction pins at its first read: each commit must still be in the read after it.
            foreach (var name in new[] { "a", "b" })
            {
                await host.PutAsync(name, cancellationToken);
                using var log = new MemoryStream();
                var read = await tap.ReadAsync(log, cancellationToken);
                await Assert.That(read.Bytes).IsGreaterThan(9);
                await Assert.That(log.ToArray()[^9]).IsEqualTo((byte)7);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task The_system_database_restores_only_at_its_snapshot_times(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var reader = host.Api.Server.Services.GetRequiredService<BackupTargets>().Platform.Reader;
        var snapshot = host.SystemSnapshot();
        // A catalog follower applies the leader's writes without capture: its next snapshot at the same position does not
        // show that nothing was written in between, so no time after a snapshot is proven.
        var next = snapshot with { At = snapshot.At.AddDays(1) };
        await Assert.That(async () => await reader.BuildAsync(BackupLayout.File(null, "local"), BackupLayout.KeyScope(null), snapshot, snapshot.At.AddHours(1), until: null, next, cancellationToken))
            .Throws<BackupHistoryIncompleteException>();

        var restored = await reader.BuildAsync(BackupLayout.File(null, "local"), BackupLayout.KeyScope(null), snapshot, snapshot.At, until: null, next, cancellationToken);
        using (restored)
        {
            await Assert.That(System.IO.File.Exists(restored.Path)).IsTrue();
        }

        await Assert.That(System.IO.File.Exists(restored.Path)).IsFalse();
    }

    private static string Within(DateTimeOffset second) => string.Create(CultureInfo.InvariantCulture,
        $"one committed in its second, {second:yyyy-MM-ddTHH:mm:ssZ} to {second.AddSeconds(1):yyyy-MM-ddTHH:mm:ssZ}, and transaction times are whole seconds; the nearest earlier time it can place is {second:yyyy-MM-ddTHH:mm:ssZ}");

    private const string NoTime = "a transaction it needs has no recorded time (its log predates them, or the clock went back within it)";

    // One node shipping only when a test runs a pass; its database holds the document seed in its generation's snapshot.
    private sealed class Host(ManagementApi api, FakeS3 s3, FakeTimeProvider time, ManagedDatabaseRef database, HttpClient data) : IAsyncDisposable
    {
        private readonly Dictionary<string, string> _texts = [with(StringComparer.Ordinal)];
        private long _stamped;

        public ManagementApi Api { get; } = api;

        public FakeTimeProvider Time { get; } = time;

        public ManagedDatabaseRef Database { get; } = database;

        public DateTimeOffset SnapshotAt { get; private set; }

        public string Generation => Api.Server.Services.GetRequiredService<IContinuousBackup>().Status.Files.Single(file => file.File == File).Generation!;

        // The generation's log the shipper has read: where its next segment starts.
        public long LogBytes => Api.Server.Services.GetRequiredService<IContinuousBackup>().Status.Files.Single(file => file.File == File).LogBytes;

        private string File => $"v1/{Database.Organization}/{Database.Project}/{Database.Id}/nodes/local";

        public TenantContext Tenant => new(Database.Organization, PlanTier.Pro, Database.Project, Database.Id);

        public static async Task<Host> StartAsync(CancellationToken cancellationToken)
        {
            var s3 = new FakeS3();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            var api = await ManagementApi.StartAsync(s3.Configure, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
            }, time);
            try
            {
                var database = await api.CreateManagedDatabaseAsync("main");
                await api.SetPlanAsync(api.User, database.Organization, "pro", cancellationToken);
                var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                var host = new Host(api, s3, time, database, data);
                await ManagementApi.CreateCollectionAsync(data, database, "people", cancellationToken);
                await host.PutAsync("seed", cancellationToken);
                // Discovery runs every ten seconds: the pass after it starts the generation with a snapshot.
                await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
                host.SnapshotAt = time.GetUtcNow();
                return host;
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        // a, b and c, each shipped as its own segment one second apart.
        public async Task<(SegmentKey S1, SegmentKey S2, SegmentKey S3)> ShipThreeSegmentsAsync(CancellationToken cancellationToken)
        {
            foreach (var name in new[] { "a", "b", "c" })
            {
                await PutAsync(name, cancellationToken);
                await ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }

            var segments = Segments(Generation);
            await Assert.That(segments.Count).IsEqualTo(3);
            return (segments[0], segments[1], segments[2]);
        }

        public async Task ShipAsync(TimeSpan advance, CancellationToken cancellationToken)
        {
            Time.Advance(advance);
            await Api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
        }

        // In key order: log position, then time.
        public IReadOnlyList<SegmentKey> Segments(string generation) =>
            [.. s3.Objects.Keys.Where(key => key.StartsWith($"{File}/wal/{generation}/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Select(key => BackupLayout.ParseSegment(key)!)];

        public string SegmentKey(long start, DateTimeOffset at) => BackupLayout.Segment(File, Ulid.Parse(Generation), start, at);

        public string SnapshotKey(DateTimeOffset at, long position) => BackupLayout.Snapshot(File, at, Ulid.Parse(Generation), position);

        // The stored bytes of an object, as a bucket-side fault sees them.
        public byte[] Raw(string key) => s3.Objects[key];

        public void StoreRaw(string key, byte[] content) => s3.StoreObject(key, content);

        // Runs check with the object key changed, then puts the original back.
        public async Task ReplaceAsync(string key, Func<byte[], byte[]> change, Func<Task> check)
        {
            var original = s3.Objects[key];
            s3.StoreObject(key, change(original));
            try
            {
                await check();
            }
            finally
            {
                s3.StoreObject(key, original);
            }
        }

        // The wrapped data key the sealed object key names.
        public string DataKeyObject(string key) =>
            BackupLayout.Key($"{Database.Organization}/{Database.Project}", Encoding.ASCII.GetString(s3.Objects[key], 4, 26));

        public byte[] Unwrap(string wrapped) => s3.Unwrap(wrapped);

        // Seals log, such as one the shipper never stores, as the object key, under the data key of the generation's snapshot.
        public async Task StoreAsync(string key, byte[] log, CancellationToken cancellationToken)
        {
            var (keyId, dataKey) = DataKey(s3.Objects.First(item => item.Key.StartsWith($"{File}/snapshots/", StringComparison.Ordinal)).Value);
            using var output = new MemoryStream();
            await BackupObject.WriteAsync(new MemoryStream(log), output, key, keyId, dataKey, cancellationToken);
            s3.StoreObject(key, output.ToArray());
        }

        // The log a stored segment holds.
        public async Task<byte[]> LogAsync(string key, CancellationToken cancellationToken)
        {
            var (_, dataKey) = DataKey(s3.Objects[key]);
            using var plain = new MemoryStream();
            await BackupObject.ReadAsync(new MemoryStream(s3.Objects[key]), plain, key, _ => Task.FromResult(dataKey), cancellationToken);
            return plain.ToArray();
        }

        // Turso stamps rows with the machine's clock: sets the second recorded for every row captured since the last call to
        // commit's, and for each transaction's first row to started's (null: no time).
        public Task StampAsync(DateTimeOffset? commit, CancellationToken cancellationToken) => StampAsync(commit, commit, cancellationToken);

        public async Task StampAsync(DateTimeOffset? started, DateTimeOffset? commit, CancellationToken cancellationToken)
        {
            var store = (TursoDatabaseStore)(await Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(Tenant, cancellationToken))!;
            _stamped = await store.WithWriterAsync(writer =>
            {
                List<(long Id, bool First)> rows = [];
                using (var select = writer.Connection.CreateCommand())
                {
                    select.CommandText = "SELECT change_id, change_type FROM turso_cdc WHERE change_id > $after ORDER BY change_id;";
                    select.Parameters.AddWithValue("$after", _stamped);
                    using var reader = select.ExecuteReader();
                    for (var first = true; reader.Read(); first = reader.GetInt64(1) == 2)
                    {
                        rows.Add((reader.GetInt64(0), first));
                    }
                }

                foreach (var (id, first) in rows)
                {
                    using var update = writer.Connection.CreateCommand();
                    update.CommandText = "UPDATE turso_cdc SET change_time = $time WHERE change_id = $id;";
                    update.Parameters.AddWithValue("$time", (first ? started : commit)?.ToUnixTimeSeconds() is { } seconds ? seconds : DBNull.Value);
                    update.Parameters.AddWithValue("$id", id);
                    update.ExecuteNonQuery();
                }

                return rows.Count > 0 ? rows[^1].Id : _stamped;
            }, "a test", cancellationToken);
        }

        // The documents written in one transaction: one commit row for all of them, like a group commit's.
        public async Task TransactAsync(string[] names, CancellationToken cancellationToken)
        {
            List<object> ops = [];
            foreach (var name in names)
            {
                _texts[name] = Faker.Lorem.Sentence(8);
                ops.Add(new { op = "put", collection = "people", id = ManagementApi.DocumentId(name), document = new { text = _texts[name] } });
            }

            using var response = await data.PostAsJsonAsync(ManagementApi.Uri(Database.Data + "/transactions"), new { ops }, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        // The keys this database's backup PUTs from now on.
        public ConcurrentQueue<string> ObservePuts()
        {
            var puts = new ConcurrentQueue<string>();
            s3.BeforePut = (key, _) =>
            {
                if (key.StartsWith(File + "/", StringComparison.Ordinal))
                {
                    puts.Enqueue(key);
                }

                return Task.CompletedTask;
            };
            return puts;
        }

        private (string KeyId, byte[] DataKey) DataKey(byte[] sealedObject)
        {
            var keyId = Encoding.ASCII.GetString(sealedObject, 4, 26);
            return (keyId, s3.Unwrap(Encoding.ASCII.GetString(s3.Objects[BackupLayout.Key($"{Database.Organization}/{Database.Project}", keyId)])));
        }

        // A real S3 DELETE through the node's own object store, as a bucket-side deletion would remove it.
        public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            await Assert.That(await Api.Server.Services.GetRequiredService<S3ObjectStore>().DeleteAsync(key, cancellationToken)).IsTrue();
            await Assert.That(s3.Objects.ContainsKey(key)).IsFalse();
        }

        public async Task PutAsync(string name, CancellationToken cancellationToken)
        {
            var text = Faker.Lorem.Sentence(8);
            using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(Database, "people", name)), new { text }, cancellationToken);
            response.EnsureSuccessStatusCode();
            _texts[name] = text;
        }

        public Task<string> CatalogAsync(CancellationToken cancellationToken) => Api.Client.GetStringAsync(ManagementApi.Uri(Database.ProjectPath + "/databases"), cancellationToken);

        public async Task<JsonElement> PlanAsync(DateTimeOffset at, CancellationToken cancellationToken)
        {
            using var response = await Api.Client.PostAsJsonAsync(ManagementApi.Uri(Database.Path + "/point-in-time/plan"), new { pointInTime = at }, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }

        public Task RefusedAsync(DateTimeOffset at, CancellationToken cancellationToken) => RefusedAsync(at, Code, Message, cancellationToken);

        public Task PlanRefusedAsync(DateTimeOffset at, HttpStatusCode status, string code, string message, CancellationToken cancellationToken) =>
            ErrorAssert.ErrorAsync(Api.Client.PostAsJsonAsync(ManagementApi.Uri(Database.Path + "/point-in-time/plan"), new { pointInTime = at }, cancellationToken), status, code, message);

        // The newest snapshot of the system database.
        public SnapshotKey SystemSnapshot() =>
            s3.Objects.Keys.Where(key => key.StartsWith(BackupLayout.Snapshots(BackupLayout.File(null, "local")), StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Select(key => BackupLayout.ParseSnapshot(key)!).Last();

        public Task RefusedAsync(DateTimeOffset at, string code, string message, CancellationToken cancellationToken) =>
            RefusedAsync(at, HttpStatusCode.Conflict, code, message, cancellationToken);

        public async Task RefusedAsync(DateTimeOffset at, HttpStatusCode status, string code, string message, CancellationToken cancellationToken)
        {
            var catalog = await CatalogAsync(cancellationToken);
            await ErrorAssert.ErrorAsync(Api.Client.PostAsJsonAsync(ManagementApi.Uri(Database.Path + "/point-in-time/restore"), new { pointInTime = at, name = "refused" }, cancellationToken),
                status, code, message);
            await Assert.That(await CatalogAsync(cancellationToken)).IsEqualTo(catalog);
        }

        // Restores at the time into a new database holding exactly the present documents, with their texts, and none of the absent.
        public async Task RestoresAsync(DateTimeOffset at, string name, string[] present, string[] absent, CancellationToken cancellationToken)
        {
            using (var response = await Api.Client.PostAsJsonAsync(ManagementApi.Uri(Database.Path + "/point-in-time/restore"), new { pointInTime = at, name }, cancellationToken))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            var restored = Database with { Name = name };
            foreach (var document in present)
            {
                using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", document)), cancellationToken);
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var body = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                await Assert.That(body.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(_texts[document]);
            }

            foreach (var document in absent)
            {
                using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", document)), cancellationToken);
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
        }

        public async ValueTask DisposeAsync()
        {
            data.Dispose();
            await Api.DisposeAsync();
        }
    }
}
