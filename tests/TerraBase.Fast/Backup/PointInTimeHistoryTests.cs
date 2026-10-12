using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Sqlite;
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
        var store = (SqliteDatabaseStore)(await host.Api.Server.Services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(tenant, cancellationToken))!;
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
            await host.StoreEmptySegmentAsync(s1.Start, host.SnapshotAt.AddMilliseconds(ms), cancellationToken);
        }

        var last = host.SnapshotAt.AddMilliseconds(300);
        var plan = await host.PlanAsync(last, cancellationToken);
        await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(3);
        await host.RestoresAsync(last, "at-empty", ["seed"], ["a"], cancellationToken);
        // s1, read after the time, starts where the empty ones end.
        await host.RestoresAsync(last.AddMilliseconds(200), "before-a", ["seed"], ["a"], cancellationToken);

        // The daily snapshot holds exactly the log an empty segment after s1 ends at.
        await host.ShipAsync(TimeSpan.FromDays(1), cancellationToken);
        await host.StoreEmptySegmentAsync(host.LogBytes, s1.At.AddHours(1), cancellationToken);
        await host.RestoresAsync(s1.At.AddHours(2), "quiet", ["seed", "a"], [], cancellationToken);
    }

    [Test]
    public async Task A_segment_starting_where_a_non_empty_one_starts_is_refused_at_restore(CancellationToken cancellationToken)
    {
        await using var host = await Host.StartAsync(cancellationToken);
        var (_, s2, s3) = await host.ShipThreeSegmentsAsync(cancellationToken);
        var duplicate = s2.At.AddMilliseconds(500);
        await host.StoreEmptySegmentAsync(s2.Start, duplicate, cancellationToken);

        // Only s2's length shows it holds log the duplicate does not continue; the plan says it checked names only.
        var plan = await host.PlanAsync(duplicate, cancellationToken);
        await Assert.That(plan.GetProperty("historyCheck").GetString()).IsEqualTo("metadata");
        foreach (var at in new[] { duplicate, s3.At })
        {
            await host.RefusedAsync(at, cancellationToken);
        }

        await host.RestoresAsync(s2.At, "at-b", ["seed", "a", "b"], ["c"], cancellationToken);
    }

    // One node shipping only when a test runs a pass; its database holds the document seed in its generation's snapshot.
    private sealed class Host(ManagementApi api, FakeS3 s3, FakeTimeProvider time, ManagedDatabaseRef database, HttpClient data) : IAsyncDisposable
    {
        private readonly Dictionary<string, string> _texts = [with(StringComparer.Ordinal)];

        public ManagementApi Api { get; } = api;

        public FakeTimeProvider Time { get; } = time;

        public ManagedDatabaseRef Database { get; } = database;

        public DateTimeOffset SnapshotAt { get; private set; }

        public string Generation => Api.Server.Services.GetRequiredService<IContinuousBackup>().Status.Files.Single(file => file.File == File).Generation!;

        // The generation's log the shipper has read: where its next segment starts.
        public long LogBytes => Api.Server.Services.GetRequiredService<IContinuousBackup>().Status.Files.Single(file => file.File == File).LogBytes;

        private string File => $"v1/{Database.Organization}/{Database.Project}/{Database.Id}";

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

        // A sealed segment holding no log, which the shipper never stores, under the data key of the generation's snapshot.
        public async Task StoreEmptySegmentAsync(long start, DateTimeOffset at, CancellationToken cancellationToken)
        {
            var key = BackupLayout.Segment(File, Ulid.Parse(Generation), start, at);
            var keyId = Encoding.ASCII.GetString(s3.Objects.First(item => item.Key.StartsWith($"{File}/snapshots/", StringComparison.Ordinal)).Value, 4, 26);
            var dataKey = s3.Unwrap(Encoding.ASCII.GetString(s3.Objects[BackupLayout.Key($"{Database.Organization}/{Database.Project}", keyId)]));
            using var output = new MemoryStream();
            await BackupObject.WriteAsync(Stream.Null, output, key, keyId, dataKey, cancellationToken);
            s3.StoreObject(key, output.ToArray());
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

        public async Task RefusedAsync(DateTimeOffset at, CancellationToken cancellationToken)
        {
            var catalog = await CatalogAsync(cancellationToken);
            await ErrorAssert.ErrorAsync(Api.Client.PostAsJsonAsync(ManagementApi.Uri(Database.Path + "/point-in-time/restore"), new { pointInTime = at, name = "refused" }, cancellationToken),
                HttpStatusCode.Conflict, Code, Message);
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
