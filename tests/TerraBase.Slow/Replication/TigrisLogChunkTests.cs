using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Backup;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Slow.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

/// <summary>
/// Log shipping through the private Tigris CI bucket (FR-50): three regions, two of which refuse a database's batches while
/// the third commits a catch-up larger than the break-even size, then catch up one after the other. Every chunk is put once,
/// sealed (no document text in the bucket), and read by both regions. Each invocation owns and deletes a unique prefix.
/// </summary>
public sealed class TigrisLogChunkTests
{
    // Documents of 3 KiB of base64: 480 ship about 1.1 MB, five times the gru break-even of two readers.
    private const int Documents = 480;

    [Test]
    public async Task A_catch_up_reaches_two_regions_through_chunks_put_once_in_Tigris(CancellationToken cancellationToken)
    {
        var prefix = $"ci/{EntityIds.New()}/";
        var bucket = new Bucket(prefix);
        var members = new FakeMemberStore();
        var machines = new List<Machine>();
        try
        {
            foreach (var region in new[] { "gru", "sjc", "fra" })
            {
                machines.Add(await Machine.StartAsync(members, bucket, region));
            }

            var (gru, sjc, fra) = (machines[0], machines[1], machines[2]);
            var organization = await gru.Api.CreateOrganizationAsync("Log chunks");
            var project = await gru.Api.CreateProjectAsync(organization, "Project");
            using var created = await gru.Api.Client.PostAsJsonAsync(
                ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main", allRegions = true }, cancellationToken);
            var body = JsonElement.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(body.GetRawText());
            var managed = new ManagedDatabaseRef(organization, project, body.GetProperty("id").GetString()!, "main");
            var tenant = new TenantContext(organization, PlanTier.Hobby, project, managed.Id);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gru.Api.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot().Peers
                .Count(peer => peer.Region is "sjc" or "fra") == 2), timeout: TimeSpan.FromSeconds(60));
            using var data = await gru.Api.CreateClientAsync(organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, managed, "items", cancellationToken);
            const string First = """{"n":0}""";
            using (var put = await data.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(managed, "items", "first")), new StringContent(First, Encoding.UTF8, "application/json"), cancellationToken))
            {
                await Assert.That(put.IsSuccessStatusCode).IsTrue().Because(await put.Content.ReadAsStringAsync(cancellationToken));
            }

            foreach (var peer in new[] { sjc, fra })
            {
                await Assert.That(await peer.UntilAsync(tenant, ManagementApi.DocumentId("first"), First)).IsTrue();
                peer.Held = tenant;
            }

            // Committed through gru's group commit, as the document API does, without waiting for a remote region to confirm.
            var store = await gru.Engine.OpenDatabaseAsync(tenant, cancellationToken) ?? throw new InvalidOperationException("gru holds the database");
            var written = new Dictionary<string, string>();
            for (var index = 0; index < Documents; index++)
            {
                var (id, json) = (EntityIds.New().ToString(), $$"""{"p":"{{Convert.ToBase64String(RandomNumberGenerator.GetBytes(3 * 768))}}"}""");
                written[id] = json;
                _ = await store.WriteAsync(async (transaction, token) =>
                {
                    _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(json), token);
                    return true;
                }, static _ => true, cancellationToken);
            }

            foreach (var peer in new[] { sjc, fra })
            {
                peer.Held = null;
                foreach (var (id, json) in written)
                {
                    await Assert.That(await peer.UntilAsync(tenant, id, json)).IsTrue().Because($"{peer.Region} caught up with {id}");
                }
            }

            var chunks = bucket.Puts.Keys.Where(key => key.StartsWith(LogChunks.Prefix, StringComparison.Ordinal)).ToList();
            await Assert.That(chunks).IsNotEmpty();
            using var s3 = TigrisCiBucket.Client();
            var marker = Encoding.UTF8.GetBytes(written.Values.First()[6..60]);
            foreach (var key in chunks)
            {
                await Assert.That(bucket.Puts[key]).IsEqualTo(1);
                await Assert.That(bucket.Readers(key)).Contains("sjc").And.Contains("fra");
                using var stored = await s3.GetObjectAsync(TigrisCiBucket.Bucket, prefix + key, cancellationToken);
                using var copy = new MemoryStream();
                await stored.ResponseStream.CopyToAsync(copy, cancellationToken);
                await Assert.That(copy.ToArray().AsSpan(0, 4).SequenceEqual("TBL1"u8) && copy.ToArray().AsSpan().IndexOf(marker) < 0).IsTrue().Because(key);
            }
        }
        finally
        {
            foreach (var machine in machines)
            {
                await machine.DisposeAsync();
            }

            using var s3 = TigrisCiBucket.Client();
            var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = TigrisCiBucket.Bucket, Prefix = prefix }, CancellationToken.None);
            foreach (var stored in listed.S3Objects ?? [])
            {
                await s3.DeleteObjectAsync(TigrisCiBucket.Bucket, stored.Key, CancellationToken.None);
            }
        }
    }

    // The CI bucket under this invocation's prefix, through the real S3 store, with each key's puts and reading regions.
    private sealed class Bucket(string prefix)
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _reads = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, int> Puts { get; } = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Readers(string key) => _reads.TryGetValue(key, out var regions) ? [.. regions.Keys] : [];

        public IObjectStore For(string region, IHttpClientFactory http)
        {
            var settings = new Dictionary<string, string>();
            TigrisCiBucket.Configure(settings);
            return new Client(this, region, prefix, new S3ObjectStore(new Uri(settings["backup-endpoint"]), settings["backup-region"], settings["backup-bucket"],
                settings["backup-access-key-id"], settings["backup-secret-key"], http, S3ObjectStore.HttpClientName, TimeProvider.System));
        }

        private sealed class Client(Bucket bucket, string region, string prefix, IObjectStore inner) : IObjectStore
        {
            public async Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
            {
                await inner.PutAsync(prefix + key, content, cancellationToken);
                _ = bucket.Puts.AddOrUpdate(key, 1, (_, count) => count + 1);
            }

            public Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken)
            {
                _ = bucket._reads.GetOrAdd(key, _ => new(StringComparer.Ordinal)).AddOrUpdate(region, 1, (_, count) => count + 1);
                return inner.ReadAsync(prefix + key, read, cancellationToken);
            }

            public async IAsyncEnumerable<StoredObject> ListAsync(string keyPrefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await foreach (var item in inner.ListAsync(prefix + keyPrefix, startAfter is null ? null : prefix + startAfter, cancellationToken))
                {
                    yield return item with { Key = item.Key.StartsWith(prefix, StringComparison.Ordinal) ? item.Key[prefix.Length..] : string.Empty };
                }
            }

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => inner.DeleteAsync(prefix + key, cancellationToken);
        }
    }

    private sealed class Machine(string region) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-log-chunks-").FullName;
        private ManagementApi? _api;

        public string Region { get; } = region;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Region} is stopped");

        public IStorageEngine Engine => Api.Server.Services.GetRequiredService<IStorageEngine>();

        // While set, this machine refuses the database's batches.
        public TenantContext? Held { get; set; }

        public static async Task<Machine> StartAsync(FakeMemberStore members, Bucket bucket, string region)
        {
            var machine = new Machine(region);
            try
            {
                machine._api = await ManagementApi.StartAsync(settings =>
                {
                    settings["region"] = region;
                    settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
                    settings["data-directory"] = machine._directory;
                }, services =>
                {
                    services.AddSingleton<IMemberDirectory>(members);
                    services.AddSingleton<IObjectStore>(provider => bucket.For(region, provider.GetRequiredService<IHttpClientFactory>()));
                    services.AddSingleton<IChangeGroupSink>(provider => new HeldSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), machine));
                });
                return machine;
            }
            catch
            {
                await machine.DisposeAsync();
                throw;
            }
        }

        public async Task<bool> UntilAsync(TenantContext database, string id, string json)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                if (await Engine.OpenDatabaseAsync(database, CancellationToken.None) is { } store)
                {
                    await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
                    if (await transaction.GetAsync("items", id, CancellationToken.None) is { Deleted: false } document && Encoding.UTF8.GetString(document.Json.Span) == json)
                    {
                        return true;
                    }
                }

                await Task.Delay(100);
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_api is not null)
            {
                await _api.DisposeAsync();
                _api = null;
            }

            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class HeldSink(IChangeGroupSink sink, Machine machine) : IChangeGroupSink
    {
        public ValueTask<Hlc?> ApplyAsync(
            TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null) =>
            database == machine.Held ? throw new IOException("the test holds this database's batches") : sink.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            sink.RepairAsync(database, incarnation, states, cancellationToken);

        public ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            sink.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
    }
}
