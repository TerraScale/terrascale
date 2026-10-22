using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;

internal static class AntiEntropyPullBench
{
    internal static async Task RunAsync(ServiceProvider services, TenantContext database, int count, byte[] expected)
    {
        var assembly = typeof(IReplicaStates).Assembly;
        var type = assembly.GetType("TerraBase.Features.Replication.AntiEntropy", throwOnError: true)!;
        var encode = type.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<RepairState>, Ulid, StatePage>>();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // shortcut: bottom states isolate receive cost; use live states when measuring sink joins.
        var replay = new ReplayPeer(DocumentState.Bottom, expected, encode, secret, services.GetRequiredService<IReplicaStates>(), database);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http2));
        _ = builder.Services.AddGrpc();
        _ = builder.Services.AddSingleton(replay);
        await using var app = builder.Build();
        _ = app.MapGrpcService<ReplayPeer>();
        await app.StartAsync().ConfigureAwait(false);
        var peer = Activator.CreateInstance(assembly.GetType("TerraBase.Engine.Cluster.ClusterMember", true)!,
            [Ulid.NewUlid().ToString(), app.Urls.Single(), "sjc", "benchmark"]);
        var credential = Activator.CreateInstance(assembly.GetType("TerraBase.Features.Replication.PeerCredential", true)!,
            BindingFlags.Instance | BindingFlags.NonPublic, null, [secret], CultureInfo.InvariantCulture);
        var checkpoint = services.GetRequiredService(assembly.GetType("TerraBase.Hosting.DatabaseLifecycleCheckpoint", true)!);
        var sink = Activator.CreateInstance(assembly.GetType("TerraBase.Features.Replication.StorageChangeGroupSink", true)!,
            [services.GetRequiredService<IStorageEngine>(), null, checkpoint]);
        using var antiEntropy = (IDisposable)Activator.CreateInstance(type,
            [null, null, services.GetRequiredService<IReplicaStates>(), sink, null, TimeProvider.System,
                services.GetRequiredService<IMeterFactory>(), services.GetRequiredService(typeof(ILogger<>).MakeGenericType(type)), credential])!;
        var pull = type.GetMethod("PullAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long pointSelects = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Name == "terrabase.storage.reads")
            {
                meter.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref pointSelects, value));
        listener.Start();
        foreach (var matched in new[] { true, false })
        {
            replay.Matched = matched;
            replay.Count = Math.Min(count, 1024);
            _ = await PullAsync().ConfigureAwait(false);
            replay.Count = count;
            Console.WriteLine(matched ? "Scope: matched PullAsync, real local and remote digests of the same warm store; no independent replica or cold-I/O claim."
                : "Scope: receive/key-retention microbenchmark through real AntiEntropy.PullAsync, no horizon, same-process loopback gRPC.");
            Console.WriteLine(matched ? "Matched peer calls the real IReplicaStates.DigestAsync; two complete scans, no repair pages."
                : "Synthetic peer forces all buckets and sends bottom states; real sink skips zero-stamp states. Excludes document reads/joins, not full repair.");
            Console.WriteLine("One warmup (1024 states for receive); five repeats. CPU includes server; page bytes = serialized StatePage, excluding transport framing.");
            Console.WriteLine("Logical state rows = verified digest cardinality per scan + instrumented point SELECTs (zero for skipped bottom states).");
            Console.WriteLine("| repeat | wall ms | process CPU ms | logical state rows | repair page bytes | allocated MiB |");
            Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: |");
            var walls = new double[5];
            var cpus = new double[5];
            var allocations = new double[5];
            using var process = Process.GetCurrentProcess();
            for (var i = 0; i < 5; i++)
            {
                _ = Interlocked.Exchange(ref pointSelects, 0);
                replay.Bytes = 0;
                replay.Sent = 0;
                process.Refresh();
                var cpuStart = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var timer = Stopwatch.StartNew();
                var changed = await PullAsync().ConfigureAwait(false);
                timer.Stop();
                process.Refresh();
                walls[i] = timer.Elapsed.TotalMilliseconds;
                cpus[i] = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                allocations[i] = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1048576.0;
                // shortcut: digest rows assume a full scan; update this count with incremental Merkle maintenance.
                var expectedRows = (matched ? 2L : 1L) * count;
                var rows = expectedRows + Interlocked.Read(ref pointSelects);
                if (changed != 0 || replay.Sent != (matched ? 0 : count) || rows != expectedRows
                    || await services.GetRequiredService<IReplicaStates>().DigestAsync(database, null, CancellationToken.None).ConfigureAwait(false) is not { } digest
                    || !digest.Buckets.AsSpan().SequenceEqual(expected))
                {
                    throw new InvalidDataException("Replay changed the fixture, missed rows, or selected an unexpected number of state rows.");
                }

                Console.WriteLine(FormattableString.Invariant($"| {i + 1} | {walls[i]:F3} | {cpus[i]:F3} | {rows} | {replay.Bytes} | {allocations[i]:F3} |"));
            }

            Console.WriteLine("| metric | median | min | max |");
            Console.WriteLine("| --- | ---: | ---: | ---: |");
            foreach (var (name, values) in new[] { ("wall ms", walls), ("process CPU ms", cpus), ("allocated MiB", allocations) })
            {
                Array.Sort(values);
                Console.WriteLine(FormattableString.Invariant($"| {name} | {values[2]:F3} | {values[0]:F3} | {values[4]:F3} |"));
            }
        }

        async Task<int?> PullAsync()
        {
            return await ((Task<int?>)pull.Invoke(antiEntropy, [database, peer, CancellationToken.None])!).ConfigureAwait(false);
        }
    }

    private sealed class ReplayPeer(DocumentState state, byte[] expected,
        Func<IReadOnlyList<RepairState>, Ulid, StatePage> encode, string secret, IReplicaStates source, TenantContext database) : TerraBase.Features.Replication.V1.Replication.ReplicationBase
    {
        private readonly Ulid _incarnation = Ulid.NewUlid();
        internal bool Matched { get; set; }
        internal int Count { get; set; }
        internal long Bytes { get; set; }
        internal int Sent { get; set; }

        public override async Task<DigestReply> Digest(DigestRequest request, ServerCallContext context)
        {
            Authenticate(context);
            var actual = Matched ? await source.DigestAsync(database, null, context.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Matched source is not held.") : null;
            var differing = actual?.Buckets ?? [.. expected];
            for (var i = 0; !Matched && i < 4096; i++)
            {
                differing[i * 16] ^= 1;
            }

            return new DigestReply
            {
                Held = true,
                Incarnation = ByteString.CopyFrom((actual?.Incarnation ?? _incarnation).ToByteArray()),
                Buckets = ByteString.CopyFrom(ApplicationEnvelope.Encode(differing).Span),
            };
        }

        public override async Task ReadBucket(BucketRequest request, IServerStreamWriter<StatePage> responseStream, ServerCallContext context)
        {
            Authenticate(context);
            if (request.Buckets.Count != 4096 || !request.Database.Incarnation.Span.SequenceEqual(_incarnation.ToByteArray()))
            {
                throw new InvalidDataException("Replay requires all buckets of the source incarnation.");
            }

            for (var offset = 0; offset < Count; offset += 1024)
            {
                var page = encode([.. Enumerable.Range(offset, Math.Min(1024, Count - offset))
                    .Select(i => new RepairState("items", i.ToString("D26", CultureInfo.InvariantCulture), state))], _incarnation);
                Bytes += page.CalculateSize();
                Sent += Math.Min(1024, Count - offset);
                await responseStream.WriteAsync(page, context.CancellationToken).ConfigureAwait(false);
            }
        }

        private void Authenticate(ServerCallContext context)
        {
            if (context.RequestHeaders.GetValue("x-terrabase-peer-credential") != secret)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Unexpected benchmark peer credential."));
            }
        }
    }
}
