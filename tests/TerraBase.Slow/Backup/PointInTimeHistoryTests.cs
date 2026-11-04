using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

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
