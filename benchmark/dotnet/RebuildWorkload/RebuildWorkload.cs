using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Benchmark
{
    /// <summary>Explicitly compiled benchmark: two real hosts in one process, production certified recovery.</summary>
    public sealed class RebuildWorkload
    {
        [Test]
        [Timeout(6 * 3600 * 1000)]
        public async Task Certified_rebuild(CancellationToken cancellationToken)
        {
            var count = int.Parse(Environment.GetEnvironmentVariable("R6_BENCH_DOCS") ?? "100000", System.Globalization.CultureInfo.InvariantCulture);
            Require(count is 100000 or 1000000, "R6_BENCH_DOCS must be 100000 or 1000000");
            var output = Environment.GetEnvironmentVariable("R6_BENCH_OUTPUT")
                ?? throw new InvalidOperationException("R6_BENCH_OUTPUT must name a new JSON result file");
            var members = new FakeMemberStore();
            var user = EntityIds.New().ToString();
            var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Enterprise, EntityIds.New().ToString(), EntityIds.New().ToString());
            var observation = new Observation(database);
            var sourceClock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var receiverClock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var source = await StartAsync("gru", members, sourceClock, user, null, cancellationToken);
            await using var receiver = await StartAsync("sjc", members, receiverClock, user, observation, cancellationToken);
            foreach (var (api, clock) in new[] { (source, sourceClock), (receiver, receiverClock) })
            {
                clock.Advance(DirectoryMembership.Heartbeat);
                _ = await api.Server.Services.GetRequiredService<IMembership>().RefreshAsync(cancellationToken);
                await RegisterAsync(api, database, api == source ? "sjc" : "gru", cancellationToken);
            }

            var sourceStore = await StoreAsync(source, database, cancellationToken);
            await using (var transaction = await sourceStore.BeginDurableTransactionAsync(cancellationToken))
            {
                _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), cancellationToken);
                _ = await transaction.CommitAsync(cancellationToken);
            }
            // Setup bypasses HTTP admission, but writes genuine engine states/CDC in bounded durable transactions.
            var payload = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', 240) + "\"}");
            for (var first = 0; first < count; first += 256)
            {
                await using var transaction = await sourceStore.BeginDurableTransactionAsync(cancellationToken);
                for (var index = first; index < Math.Min(first + 256, count); index++)
                {
                    _ = await transaction.PutAsync("items", DocumentId(index), payload, cancellationToken);
                }
                _ = await transaction.CommitAsync(cancellationToken);
                if ((first + 256) % 16384 == 0)
                {
                    Console.WriteLine($"R6 setup {first + 256}/{count}");
                }
            }
            var expected = await ScanAsync(source, database, cancellationToken);
            Require(expected.Count == count, "source setup count differs");
            await UntilAsync(async () => await SameDigestAsync(source, receiver, database, cancellationToken), cancellationToken);
            Require(await ScanAsync(receiver, database, cancellationToken) == expected, "receiver setup differs");

            var storage = receiver.Server.Services.GetRequiredService<IRebuildStorage>();
            var old = (await StoreAsync(receiver, database, cancellationToken)).Incarnation;
            using var samplerStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sampling = SampleAsync(samplerStop.Token);
            var allocated = GC.GetTotalAllocatedBytes();
            Console.WriteLine($"R6 certified recovery starting for {count} documents");
            var started = Stopwatch.GetTimestamp();
            RebuildOutcome outcome;
            (long Rss, long Managed) peak;
            double total;
            try
            {
                await receiver.Server.Services.GetRequiredService<IReplicaCopyFaults>()
                    .RecordAsync(database, old, "explicit benchmark copy fault", cancellationToken);
                outcome = await observation.Completed.Task.WaitAsync(cancellationToken);
                total = Stopwatch.GetElapsedTime(started).TotalSeconds;
            }
            finally
            {
                await samplerStop.CancelAsync();
                peak = await sampling;
            }
            allocated = GC.GetTotalAllocatedBytes() - allocated;
            Console.WriteLine($"R6 recovery outcome {outcome.GetType().Name} after {total:F3} seconds");
            if (outcome is not RebuildOutcome.Completed)
            {
                await using var failure = new FileStream(output + ".failure.json", FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var diagnostic = new Utf8JsonWriter(failure);
                diagnostic.WriteStartObject();
                diagnostic.WriteString("host", Environment.MachineName);
                diagnostic.WriteString("scope", "two real Kestrel hosts in one process; recovery-only samples; no completed certification or post-recovery proof");
                diagnostic.WriteNumber("docs", count);
                diagnostic.WriteString("outcome", outcome.GetType().Name);
                if (outcome is RebuildOutcome.Refused refused)
                {
                    diagnostic.WriteString("reason", refused.Reason);
                }
                diagnostic.WriteNumber("total_seconds", total);
                diagnostic.WriteNumber("rebuild_seconds", observation.RebuildSeconds);
                diagnostic.WriteNumber("refill_seconds", observation.RefillSeconds);
                diagnostic.WriteNumber("sampled_process_peak_rss_bytes", peak.Rss);
                diagnostic.WriteNumber("sampled_process_peak_managed_bytes", peak.Managed);
                diagnostic.WriteNumber("process_allocated_bytes", allocated);
                diagnostic.WriteBoolean("certified", false);
                diagnostic.WriteEndObject();
                await diagnostic.FlushAsync(cancellationToken);
            }
            Require(outcome is RebuildOutcome.Completed, "production recovery did not complete: " + outcome);
            var rebuilt = ((RebuildOutcome.Completed)outcome).NewIncarnation;
            Require(rebuilt != old && (await StoreAsync(receiver, database, cancellationToken)).Incarnation == rebuilt, "incarnation did not change");
            Require(await receiver.Server.Services.GetRequiredService<IRebuildLedger>().GetAsync(database, cancellationToken) is null, "ledger not retired");
            Require(!storage.SideExists(storage.SidePath(database, old)), "retained side copy still exists");
            var faults = receiver.Server.Services.GetRequiredService<IReplicaCopyFaults>();
            Require(await faults.RefusedAsync(database, old, cancellationToken), "old incarnation accepts writes");
            Require(!await faults.RefusedAsync(database, rebuilt, cancellationToken), "rebuilt incarnation refuses writes");
            Require(await ScanAsync(receiver, database, cancellationToken) == expected, "rebuilt documents differ");
            await UntilAsync(async () => await SameDigestAsync(source, receiver, database, cancellationToken), cancellationToken);
            await CertificateAsync(receiver, source, database, cancellationToken);

            var marker = DocumentId(count);
            var catchupStart = Stopwatch.GetTimestamp();
            await using (var transaction = await sourceStore.BeginDurableTransactionAsync(cancellationToken))
            {
                _ = await transaction.PutAsync("items", marker, "{\"after\":true}"u8.ToArray(), cancellationToken);
                _ = await transaction.CommitAsync(cancellationToken);
            }
            await UntilAsync(async () =>
            {
                await using var transaction = await (await StoreAsync(receiver, database, cancellationToken)).BeginTransactionAsync(false, cancellationToken);
                return await transaction.GetAsync("items", marker, cancellationToken) is { Deleted: false } document
                    && document.Json.Span.SequenceEqual("{\"after\":true}"u8);
            }, cancellationToken);
            var catchup = Stopwatch.GetElapsedTime(catchupStart).TotalSeconds;
            await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new Utf8JsonWriter(file);
            writer.WriteStartObject();
            writer.WriteString("host", Environment.MachineName);
            writer.WriteString("scope", "two real Kestrel hosts in one process; sampled RSS, managed memory and allocations include both; setup and full verification excluded");
            writer.WriteNumber("docs", count);
            writer.WriteNumber("total_seconds", total);
            writer.WriteNumber("rebuild_seconds", observation.RebuildSeconds);
            writer.WriteNumber("refill_seconds", observation.RefillSeconds);
            writer.WriteNumber("post_completion_write_visible_seconds", catchup);
            writer.WriteNumber("sampled_process_peak_rss_bytes", peak.Rss);
            writer.WriteNumber("sampled_process_peak_managed_bytes", peak.Managed);
            writer.WriteNumber("process_allocated_bytes", allocated);
            writer.WriteString("document_digest", expected.Hash);
            writer.WriteBoolean("certified", true);
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);
        }

        private static Task<ManagementApi> StartAsync(string region, FakeMemberStore members, FakeTimeProvider clock, string user,
            Observation? observation, CancellationToken token)
        {
            return ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = EntityIds.New().ToString();
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(members);
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreams>(provider, TimeProvider.System));
                if (observation is not null)
                {
                    var original = services.Last(service => service.ServiceType == typeof(IRebuildCertification));
                    services.AddSingleton<IRebuildCertification>(provider => new TimedCertification(
                        (IRebuildCertification)(original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(provider)
                            ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)), observation));
                    services.AddSingleton<IReplicaRebuild>(provider => new TimedRebuild(ActivatorUtilities.CreateInstance<ReplicaRebuild>(provider), observation));
                }
            }, userId: user, cancellationToken: token);
        }

        private static async Task RegisterAsync(ManagementApi api, TenantContext database, string replica, CancellationToken token)
        {
            await CatalogFixture.RegisterProjectAsync(api, api.User, database.OrganizationId, database.ProjectId!, token);
            await api.SetPlanAsync(api.User, database.OrganizationId, "enterprise", token);
            await CatalogFixture.RegisterAsync(api, api.User, database, "main", [replica], token);
        }

        private static async Task<IDatabaseStore> StoreAsync(ManagementApi api, TenantContext database, CancellationToken token)
        {
            return await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token)
                ?? throw new InvalidOperationException("benchmark database missing");
        }

        private static async Task<bool> SameDigestAsync(ManagementApi source, ManagementApi receiver, TenantContext database, CancellationToken token)
        {
            var first = await source.Server.Services.GetRequiredService<IReplicaStates>().DigestAsync(database, null, token);
            var second = await receiver.Server.Services.GetRequiredService<IReplicaStates>().DigestAsync(database, null, token);
            return first is not null && second is not null && first.Buckets.AsSpan().SequenceEqual(second.Buckets);
        }

        private static async Task<(int Count, string Hash)> ScanAsync(ManagementApi api, TenantContext database, CancellationToken token)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var store = await StoreAsync(api, database, token);
            string? after = null;
            var count = 0;
            while (true)
            {
                await using var transaction = await store.BeginTransactionAsync(false, token);
                var page = await transaction.ListAsync("items", after, 128, 2 * 1024 * 1024, token);
                foreach (var document in page.Documents)
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(document.Id));
                    hash.AppendData(document.Json.Span);
                    after = document.Id;
                    count++;
                }
                if (!page.HasMore)
                {
                    return (count, Convert.ToHexStringLower(hash.GetHashAndReset()));
                }
                Require(page.Documents.Count > 0, "listing made no progress");
            }
        }

        private static async Task CertificateAsync(ManagementApi receiver, ManagementApi source, TenantContext database, CancellationToken token)
        {
            var digest = await source.Server.Services.GetRequiredService<IReplicaStates>().DigestAsync(database, null, token)
                ?? throw new InvalidOperationException("source digest missing");
            var name = source.Server.Services.GetRequiredService<IMembership>().Self.Name;
            var store = (TursoDatabaseStore)(await receiver.Server.Services.GetRequiredService<TursoStorageEngine>()
                .OpenDatabaseAsync(database, token) ?? throw new InvalidOperationException("benchmark database missing"));
            await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT origin, incarnation, head FROM _tb_refill;";
                using var reader = command.ExecuteReader();
                Require(reader.Read() && reader.GetString(0) == name && reader.GetString(1) == digest.Incarnation.ToString()
                    && reader.GetInt64(2) == digest.Head && !reader.Read(), "refill certificate differs");
                return true;
            }, "benchmark certificate verification", token);
        }

        private static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (!await condition())
            {
                _ = await timer.WaitForNextTickAsync(token);
            }
        }

        private static async Task<(long Rss, long Managed)> SampleAsync(CancellationToken token)
        {
            using var process = Process.GetCurrentProcess();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            var peak = 0L;
            var managedPeak = 0L;
            try
            {
                do
                {
                    process.Refresh();
                    peak = Math.Max(peak, process.WorkingSet64);
                    managedPeak = Math.Max(managedPeak, GC.GetTotalMemory(forceFullCollection: false));
                } while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return (peak, managedPeak);
        }

        private static string DocumentId(int index)
        {
            Span<byte> bytes = stackalloc byte[16];
            bytes.Clear();
            // Fixed 48-bit millisecond timestamp and an increasing 80-bit suffix: identical workload keys across revisions.
            BinaryPrimitives.WriteUInt64BigEndian(bytes, 1_791_878_400_000UL << 16);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], checked((ulong)index + 1));
            return new Ulid(bytes).ToString();
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class Observation(TenantContext database)
        {
            public bool Watches(TenantContext candidate)
            {
                return candidate.OrganizationId == database.OrganizationId
                    && candidate.ProjectId == database.ProjectId && candidate.DatabaseId == database.DatabaseId;
            }
            public TaskCompletionSource<RebuildOutcome> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public double RebuildSeconds { get; set; }
            public double RefillSeconds { get; set; }
        }

        private sealed class TimedRebuild(IReplicaRebuild inner, Observation observation) : IReplicaRebuild
        {
            public async ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var result = await inner.RebuildAsync(database, faultedIncarnation, cancellationToken);
                    if (observation.Watches(database) && result is not RebuildOutcome.Busy)
                    {
                        observation.RebuildSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                        observation.Completed.TrySetResult(result);
                    }
                    return result;
                }
                catch (Exception error)
                {
                    if (observation.Watches(database))
                    {
                        observation.Completed.TrySetException(error);
                    }
                    throw;
                }
            }
        }

        private sealed class TimedCertification(IRebuildCertification inner, Observation observation) : IRebuildCertification
        {
            public async ValueTask<bool> RefillAsync(TenantContext database, Ulid newIncarnation, CancellationToken cancellationToken)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    return await inner.RefillAsync(database, newIncarnation, cancellationToken);
                }
                finally
                {
                    if (observation.Watches(database))
                    {
                        observation.RefillSeconds += Stopwatch.GetElapsedTime(started).TotalSeconds;
                    }
                }
            }

            public ValueTask<bool> CompleteAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken)
            {
                return inner.CompleteAsync(database, oldIncarnation, newIncarnation, cancellationToken);
            }
        }
    }
}
