using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Api;
using ZoneTree.Compression;
using ZoneTree.Options;

namespace TerraBase.Benchmarks;

/// <summary>
/// The MagicOnion echo contract used by the transport comparison. MagicOnion
/// generates the client proxy from this interface, so it must stay public.
/// </summary>
[ServiceName("terrabase.bench.TransportEcho")]
public interface ITransportEchoService : IService<ITransportEchoService>
{
    /// <summary>Echoes the request payload back to the caller.</summary>
    UnaryResult<byte[]> EchoAsync(byte[] payload);

    /// <summary>
    /// Streams a one-way bulk transfer to the client. The request is a
    /// 13-byte body: a one-byte bulk op, an 8-byte big-endian total, and a
    /// 4-byte big-endian chunk size.
    /// </summary>
    Task<ServerStreamingResult<byte[]>> BulkAsync(byte[] request);
}

/// <summary>The MagicOnion server implementation of the echo contract.</summary>
public sealed class TransportEchoService : ServiceBase<ITransportEchoService>, ITransportEchoService
{
    /// <inheritdoc />
    public UnaryResult<byte[]> EchoAsync(byte[] payload) => UnaryResult.FromResult(payload);

    /// <inheritdoc />
    public async Task<ServerStreamingResult<byte[]>> BulkAsync(byte[] request)
    {
        var (totalBytes, chunkBytes) = TransportComparisonWorkload.ReadBulkRequest(request, "magic-onion");
        var context = GetServerStreamingContext<byte[]>();
        await TransportComparisonWorkload.SendBulkFramesAsync(
            totalBytes,
            chunkBytes,
            (buffer, take) => context.WriteAsync(take == buffer.Length ? buffer : buffer.AsMemory(0, take).ToArray())).ConfigureAwait(false);
        return context.Result();
    }
}

/// <summary>
/// Compares loopback request/response transports over identical echo
/// workloads: plain gRPC unary over cleartext HTTP/2, MagicOnion unary over
/// the same stack, a custom reliable-UDP protocol built directly on UDP
/// datagrams, and QUIC (System.Net.Quic over libmsquic). The grpc+lz4,
/// rudp+lz4 and quic+lz4 lanes reuse the plain, RUDP and QUIC echo paths
/// with client-side LZ4 compression around the echo. Client and servers run in this process on
/// separate sockets.
/// </summary>
public static class TransportComparisonWorkload
{
    private const string WorkloadName = "transport-comparison";
    private const int MaximumDatagramPayloadBytes = 65_507 - RudpHeaderBytes;
    private const int RudpHeaderBytes = 12;
    private const int RudpFragmentHeaderBytes = RudpHeaderBytes + 2;
    private const int RudpFragmentPayloadBytes = 60_000;
    private const int MaximumFrameBytes = 16 * 1024 * 1024;
    private const int SocketBufferBytes = 4 * 1024 * 1024;
    // Bulk phase framing. Every lane's bulk request carries a 13-byte body
    // (op, 8-byte big-endian total, 4-byte big-endian chunk); RUDP then
    // reuses its existing frame headers with an op byte per frame.
    private const byte BulkRequestOp = 1;
    private const int BulkRequestBytes = 1 + 8 + 4;
    private const byte RudpBulkAckOp = 2;
    private const int RudpBulkAckBytes = 1 + 8;
    private static readonly SslApplicationProtocol QuicApplicationProtocol = new("terrabase-bench");
    private static readonly TimeSpan QuicIdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QuicEchoTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QuicBulkTimeout = TimeSpan.FromMinutes(10);
    private const int RudpBulkBatchFrames = 64;
    private static readonly TimeSpan RudpBulkBatchTimeout = TimeSpan.FromSeconds(1);
    private const int RudpBulkMaxStalls = 300;
    // Below the 65507 - 12 hard datagram ceiling; matches the largest echo
    // payload the default payload set uses.
    private const int RudpBulkMaxFrameBytes = 61_440;
    private const int BulkWarmupBytes = 1_048_576;
    private const int Lz4CompressionLevel = CompressionLevels.LZ4Fastest;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns whether this workload owns the requested command.</summary>
    public static bool IsRequested(string[] args) =>
        ReadOption(args, "workload")?.Equals(WorkloadName, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Runs the four-transport echo comparison and writes its receipt.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        TransportComparisonOptions options;
        try
        {
            options = TransportComparisonOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid transport comparison configuration: {exception.Message}");
            TransportComparisonOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            TransportComparisonOptions.PrintHelp();
            return 0;
        }

        var configuration = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (!string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Transport comparison requires a Release artifact.");
            return 2;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var startedAt = DateTimeOffset.UtcNow;
        var environment = BuildEnvironmentIdentity();
        var samples = new List<TransportComparisonSample>(options.Samples);

        var rawPath = Path.Combine(outputDirectory, "raw-samples.jsonl");
        await using (var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            TransportComparisonReceipt receipt;
            try
            {
                for (var index = 0; index < options.Samples; index++)
                {
                    var sample = await RunSampleAsync(options, index + 1).ConfigureAwait(false);
                    samples.Add(sample);
                    await raw.WriteLineAsync(JsonSerializer.Serialize(sample, RawJsonOptions)).ConfigureAwait(false);
                    await raw.FlushAsync().ConfigureAwait(false);
                    Console.WriteLine($"Transport comparison sample {index + 1}/{options.Samples} complete.");
                }

                receipt = new TransportComparisonReceipt(
                    "complete",
                    startedAt,
                    DateTimeOffset.UtcNow,
                    options,
                    samples,
                    rawPath,
                    Environment.CommandLine,
                    outputDirectory,
                    environment,
                    Error: null);
            }
            catch (Exception exception)
            {
                receipt = new TransportComparisonReceipt(
                    "failed",
                    startedAt,
                    DateTimeOffset.UtcNow,
                    options,
                    samples,
                    rawPath,
                    Environment.CommandLine,
                    outputDirectory,
                    environment,
                    exception.ToString());
            }

            await WriteReceiptAsync(receipt, outputDirectory).ConfigureAwait(false);
            return receipt.Status == "complete" ? 0 : 1;
        }
    }

    /// <summary>A placeholder row for a lane/payload combination a transport cannot carry.</summary>
    private static TransportEchoMeasurement UnsupportedMeasurement(
        string lane,
        string phase,
        int payloadBytes,
        int operations,
        double? compressionRatio) => new(
            lane,
            phase,
            payloadBytes,
            operations,
            Workers: phase == "throughput" ? 0 : 1,
            P50Microseconds: null,
            P95Microseconds: null,
            P99Microseconds: null,
            MeanMicroseconds: null,
            OperationsPerSecond: null,
            PayloadMegabytesPerSecond: null,
            Retransmits: null,
            CompressionRatio: compressionRatio);

    /// <summary>The compressed/original byte ratio of one probe compress.</summary>
    private static double? CompressProbe(byte[] corpus) =>
        LZ4DataCompression.Compress(corpus, Lz4CompressionLevel).Length / (double)corpus.Length;

    [SuppressMessage(
        "Interoperability",
        "CA1416:ValidatePlatformCompatibility",
        Justification = "The transport comparison targets Linux benchmark hosts; the QUIC lanes require libmsquic at runtime.")]
    private static async Task<TransportComparisonSample> RunSampleAsync(TransportComparisonOptions options, int sample)
    {
        await using var grpcHost = await StartGrpcHostAsync().ConfigureAwait(false);
        await using var magicOnionHost = await StartMagicOnionHostAsync().ConfigureAwait(false);
        using var rudpServer = new RudpEchoServer();
        await using var grpcClient = new GrpcEchoClient(HostPort(grpcHost));
        await using var magicOnionClient = new MagicOnionEchoClient(HostPort(magicOnionHost));
        await using var rudpClient = new RudpEchoClient(rudpServer.Port, options.RetransmitInitialMilliseconds, options.RetransmitMaxAttempts);

        // Rotate the lane order per sample so no lane keeps a fixed position
        // advantage (JIT warmth, allocator state) across every sample.
        var lanes = new[] { "grpc", "grpc+lz4", "magic-onion", "rudp", "quic", "rudp+lz4", "quic+lz4" };
        var rotation = (sample - 1) % lanes.Length;
        var orderedLanes = lanes.Skip(rotation).Concat(lanes.Take(rotation));

        var measurements = new List<TransportEchoMeasurement>();
        var quicPreflightError = await QuicPreflightAsync().ConfigureAwait(false);
        if (quicPreflightError is not null)
            Console.WriteLine($"Transport comparison: {quicPreflightError}");
        foreach (var payloadBytes in options.PayloadBytes)
        {
            var payload = BuildPayload(sample, payloadBytes);
            var corpus = BuildCompressiblePayload(sample, payloadBytes);
            foreach (var lane in orderedLanes)
            {
                var isLz4 = lane.EndsWith("+lz4", StringComparison.Ordinal);
                if (lane is "quic" or "quic+lz4")
                {
                    var quicLanePayload = isLz4 ? corpus : payload;
                    var ratio = isLz4 ? CompressProbe(corpus) : null;
                    if (quicPreflightError is not null)
                    {
                        Console.WriteLine($"{lane}: skipping ({quicPreflightError}).");
                        measurements.Add(UnsupportedMeasurement(lane, "latency", quicLanePayload.Length, options.LatencyOperations, ratio));
                        measurements.Add(UnsupportedMeasurement(lane, "throughput", quicLanePayload.Length, options.ThroughputOperations, ratio));
                        continue;
                    }

                    // A fresh pair per lane occurrence keeps every phase on
                    // young, single-purpose connections; the TLS 1.3
                    // handshake lands in the unmeasured warmup.
                    await using var quicServer = await QuicEchoServer.StartAsync().ConfigureAwait(false);
                    await using var quicClient = await QuicEchoClient.ConnectAsync(quicServer).ConfigureAwait(false);
                    EchoDelegate quicEcho = lane == "quic"
                        ? quicClient.EchoAsync
                        : message => EchoCompressedAsync(quicClient.EchoAsync, message);
                    measurements.Add(await MeasureLatencyAsync(lane, quicEcho, quicLanePayload, options, ratio).ConfigureAwait(false));
                    measurements.Add(await MeasureThroughputAsync(lane, quicEcho, quicLanePayload, options, rudpClient, ratio).ConfigureAwait(false));
                continue;
                }

                EchoDelegate echo = lane switch
                {
                    "grpc" => grpcClient.EchoAsync,
                    "grpc+lz4" => message => EchoCompressedAsync(grpcClient.EchoAsync, message),
                    "magic-onion" => magicOnionClient.EchoAsync,
                    "rudp+lz4" => message => EchoCompressedAsync(rudpClient.EchoAsync, message),
                    _ => rudpClient.EchoAsync,
                };
                double? compressionRatio = null;
                if (isLz4)
                    compressionRatio = LZ4DataCompression.Compress(corpus, Lz4CompressionLevel).Length / (double)corpus.Length;

                // The lz4 lanes measure the compressible corpus, not the
                // deterministic pattern payload; their ratio is computed from
                // a probe compress outside the measured windows.
                var lanePayload = isLz4 ? corpus : payload;
                measurements.Add(await MeasureLatencyAsync(lane, echo, lanePayload, options, compressionRatio).ConfigureAwait(false));
                measurements.Add(await MeasureThroughputAsync(lane, echo, lanePayload, options, rudpClient, compressionRatio).ConfigureAwait(false));
            }
        }

        // Raw one-way bulk phase: server-to-client transfer sized by the bulk
        // options, not the echo payload set.
        async Task<BulkResult> RudpBulkAsync(long totalBytes, int chunkBytes)
        {
            var batchResends = rudpServer.BulkRetransmits;
            var ackResends = rudpClient.BulkAckResends;
            var result = await rudpClient.BulkAsync(totalBytes, chunkBytes).ConfigureAwait(false);
            return result with
            {
                Retransmits = (rudpServer.BulkRetransmits - batchResends) + (rudpClient.BulkAckResends - ackResends),
            };
        }

        foreach (var lane in orderedLanes.Where(static lane => lane is "grpc" or "magic-onion" or "rudp" or "quic"))
        {
            if (lane == "quic")
            {
                if (quicPreflightError is not null)
                {
                    Console.WriteLine($"quic: skipping bulk ({quicPreflightError}).");
                    measurements.Add(UnsupportedMeasurement(lane, "bulk", options.BulkChunkBytes, 0, null));
                    continue;
                }

                await using var quicServer = await QuicEchoServer.StartAsync().ConfigureAwait(false);
                await using var quicClient = await QuicEchoClient.ConnectAsync(quicServer).ConfigureAwait(false);
                measurements.Add(await MeasureBulkAsync(lane, quicClient.BulkAsync, options).ConfigureAwait(false));
                continue;
            }

            BulkDelegate bulk = lane switch
            {
                "grpc" => grpcClient.BulkAsync,
                "magic-onion" => magicOnionClient.BulkAsync,
                _ => RudpBulkAsync,
            };
            measurements.Add(await MeasureBulkAsync(lane, bulk, options).ConfigureAwait(false));
        }

        return new TransportComparisonSample(sample, measurements);
    }

    private static async Task<TransportEchoMeasurement> MeasureLatencyAsync(
        string lane,
        EchoDelegate echo,
        byte[] payload,
        TransportComparisonOptions options,
        double? compressionRatio = null)
    {
        for (var index = 0; index < options.WarmupOperations; index++)
        {
            _ = await echo(payload).ConfigureAwait(false);
        }

        var latencies = new List<double>(options.LatencyOperations);
        for (var index = 0; index < options.LatencyOperations; index++)
        {
            var operationStart = Stopwatch.GetTimestamp();
            var response = await echo(payload).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(operationStart);
            VerifyResponse(lane, payload, response);
            latencies.Add(elapsed.TotalMicroseconds);
        }

        return new TransportEchoMeasurement(
            lane,
            "latency",
            payload.Length,
            options.LatencyOperations,
            Workers: 1,
            P50Microseconds: Percentile(latencies, 0.50),
            P95Microseconds: Percentile(latencies, 0.95),
            P99Microseconds: Percentile(latencies, 0.99),
            MeanMicroseconds: latencies.Average(),
            OperationsPerSecond: null,
            PayloadMegabytesPerSecond: null,
            Retransmits: null,
            CompressionRatio: compressionRatio);
    }

    private static async Task<TransportEchoMeasurement> MeasureThroughputAsync(
        string lane,
        EchoDelegate echo,
        byte[] payload,
        TransportComparisonOptions options,
        RudpEchoClient rudpClient,
        double? compressionRatio = null)
    {
        var retransmitBaseline = lane.StartsWith("rudp", StringComparison.Ordinal) ? rudpClient.Retransmits : 0;
        await WarmConcurrentPathAsync(echo, payload, options).ConfigureAwait(false);
        var remaining = -1;
        var workers = new Task[options.ThroughputWorkers];
        var start = Stopwatch.GetTimestamp();
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = Task.Run(async () =>
            {
                while (true)
                {
                    if (Interlocked.Increment(ref remaining) >= options.ThroughputOperations)
                        return;
                    var response = await echo(payload).ConfigureAwait(false);
                    VerifyResponse(lane, payload, response);
                }
            });
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(start);
        // The QUIC lane reports no retransmit count: loss recovery happens
        // inside QUIC and is not observable from this harness.
        long? retransmits = lane switch
        {
            "rudp" or "rudp+lz4" => rudpClient.Retransmits - retransmitBaseline,
            "quic" or "quic+lz4" => null,
            _ => 0,
        };
        var seconds = elapsed.TotalSeconds;
        return new TransportEchoMeasurement(
            lane,
            "throughput",
            payload.Length,
            options.ThroughputOperations,
            options.ThroughputWorkers,
            P50Microseconds: null,
            P95Microseconds: null,
            P99Microseconds: null,
            MeanMicroseconds: null,
            OperationsPerSecond: options.ThroughputOperations / seconds,
            PayloadMegabytesPerSecond: options.ThroughputOperations * payload.Length / seconds / (1024.0 * 1024.0),
            Retransmits: retransmits,
            CompressionRatio: compressionRatio);
    }

    /// <summary>
    /// Exercises the concurrent call path (simultaneous streams, worker
    /// dispatch) before the timed window so the first timed operations are
    /// not paying its one-time costs.
    /// </summary>
    private static async Task WarmConcurrentPathAsync(
        EchoDelegate echo,
        byte[] payload,
        TransportComparisonOptions options)
    {
        var warmup = new Task[options.ThroughputWorkers];
        for (var index = 0; index < warmup.Length; index++)
        {
            warmup[index] = Task.Run(async () =>
            {
                for (var operation = 0; operation < 16; operation++)
                    _ = await echo(payload).ConfigureAwait(false);
            });
        }

        await Task.WhenAll(warmup).ConfigureAwait(false);
    }

    /// <summary>
    /// Measures the raw one-way bulk phase: the server streams a fixed total
    /// of chunk-sized frames to the client, which times the window from
    /// request-sent to last-verified-byte. Connection setup and a 1 MiB warm
    /// pass per lane are excluded.
    /// </summary>
    private static async Task<TransportEchoMeasurement> MeasureBulkAsync(
        string lane,
        BulkDelegate bulk,
        TransportComparisonOptions options)
    {
        // RUDP bulk frames must fit one datagram; larger chunks clamp.
        var chunkBytes = lane == "rudp"
            ? Math.Min(options.BulkChunkBytes, RudpBulkMaxFrameBytes)
            : options.BulkChunkBytes;
        var totalBytes = options.BulkBytes;
        _ = await bulk(BulkWarmupBytes, chunkBytes).ConfigureAwait(false);
        var start = Stopwatch.GetTimestamp();
        var result = await bulk(totalBytes, chunkBytes).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(start);
        return new TransportEchoMeasurement(
            lane,
            "bulk",
            chunkBytes,
            result.Frames,
            Workers: 1,
            P50Microseconds: null,
            P95Microseconds: null,
            P99Microseconds: null,
            MeanMicroseconds: null,
            OperationsPerSecond: null,
            PayloadMegabytesPerSecond: totalBytes / elapsed.TotalSeconds / (1024.0 * 1024.0),
            Retransmits: result.Retransmits,
            CompressionRatio: null);
    }

    private static byte[] BuildPayload(int sample, int payloadBytes)
    {
        var payload = new byte[payloadBytes];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)(index * 31 + sample);
        return payload;
    }

    /// <summary>
    /// Builds the compressible corpus for the lz4 lanes: synthetic JSON-like
    /// records whose repeated field names and string values give LZ4 long
    /// matches while per-record ids, counters, remainders, and an xorshift
    /// trace id keep every record distinct (roughly a quarter incompressible),
    /// so a decompressed echo is a meaningful byte compare. Deterministic per
    /// (sample, size).
    /// </summary>
    private static byte[] BuildCompressiblePayload(int sample, int payloadBytes)
    {
        var builder = new StringBuilder(payloadBytes + 256);
        for (var record = 0; builder.Length < payloadBytes; record++)
        {
            var entropy = Mix((uint)(sample * 374_761_393 + record * 668_265_263));
            builder.Append(CultureInfo.InvariantCulture, $"{{\"id\":\"rec-{sample}-{record:D8}\",\"trace\":\"{entropy:X8}{Mix(~entropy):X8}\",\"region\":\"zone-{record % 7}\",");
            builder.Append(CultureInfo.InvariantCulture, $"\"counter\":{100_000 + record},\"status\":\"active\",\"tags\":[\"telemetry\",\"sync\",\"replication\"],");
            builder.Append(CultureInfo.InvariantCulture, $"\"weight\":{(record * 37) % 1_000}.5,\"owner\":\"terrabase-node-{record % 13}\"}},");
        }

        return Encoding.ASCII.GetBytes(builder.ToString(0, payloadBytes));

        static uint Mix(uint value)
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            return value;
        }
    }

    /// <summary>
    /// Wraps an echo lane with client-side LZ4: compresses the corpus, sends
    /// the compressed bytes through the unchanged echo path, and returns the
    /// decompressed response. Codec CPU is inside the caller's measured
    /// window by design.
    /// </summary>
    private static async Task<byte[]> EchoCompressedAsync(EchoDelegate echo, byte[] corpus)
    {
        var compressed = LZ4DataCompression.Compress(corpus, Lz4CompressionLevel).ToArray();
        var response = await echo(compressed).ConfigureAwait(false);
        return LZ4DataCompression.Decompress(response, corpus.Length);
    }

    private static void VerifyResponse(string lane, byte[] payload, byte[] response)
    {
        if (response.Length != payload.Length || !payload.AsSpan().SequenceEqual(response))
            throw new InvalidOperationException($"The {lane} lane returned a corrupted echo response.");
    }

    private delegate Task<byte[]> EchoDelegate(byte[] payload);

    private delegate Task<BulkResult> BulkDelegate(long totalBytes, int chunkBytes);

    private readonly record struct BulkResult(int Frames, long? Retransmits);

    /// <summary>Encodes the bulk request body every lane shares.</summary>
    private static byte[] BuildBulkRequest(long totalBytes, int chunkBytes)
    {
        var request = new byte[BulkRequestBytes];
        request[0] = BulkRequestOp;
        BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(1, 8), totalBytes);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(9, 4), chunkBytes);
        return request;
    }

    internal static (long TotalBytes, int ChunkBytes) ReadBulkRequest(ReadOnlySpan<byte> request, string lane)
    {
        if (request.Length != BulkRequestBytes || request[0] != BulkRequestOp)
            throw new InvalidOperationException($"The {lane} bulk lane received a malformed request ({request.Length} bytes).");
        var totalBytes = BinaryPrimitives.ReadInt64BigEndian(request.Slice(1, 8));
        var chunkBytes = BinaryPrimitives.ReadInt32BigEndian(request.Slice(9, 4));
        if (totalBytes <= 0 || chunkBytes <= 0)
            throw new InvalidOperationException($"The {lane} bulk lane received an empty bulk request.");
        return (totalBytes, chunkBytes);
    }

    private static long BulkFrameCount(long totalBytes, int chunkBytes) => (totalBytes + chunkBytes - 1) / chunkBytes;

    private static int BulkFrameLength(long totalBytes, int chunkBytes, long sequence) =>
        sequence == BulkFrameCount(totalBytes, chunkBytes) - 1
            ? (int)(totalBytes - (sequence * chunkBytes))
            : chunkBytes;

    /// <summary>Drives every message-based bulk sender (gRPC, MagicOnion): fills one reused chunk buffer and writes frame-sized slices, stamping each frame's first 8 payload bytes with its big-endian sequence so corruption is detectable.</summary>
    internal static async Task SendBulkFramesAsync(long totalBytes, int chunkBytes, Func<byte[], int, Task> writeAsync)
    {
        var chunk = BuildBulkChunk(chunkBytes);
        long sent = 0;
        long sequence = 0;
        while (sent < totalBytes)
        {
            var take = BulkFrameLength(totalBytes, chunkBytes, sequence);
            BinaryPrimitives.WriteInt64BigEndian(chunk.AsSpan(0, 8), sequence++);
            // Message writers take whole arrays; the short final frame needs
            // its own copy so the reused buffer is not truncated.
            await writeAsync(chunk, take).ConfigureAwait(false);
            sent += take;
        }
    }

    private static byte[] BuildBulkChunk(int chunkBytes)
    {
        var chunk = new byte[chunkBytes];
        FillBulkChunk(chunk);
        return chunk;
    }

    private static void FillBulkChunk(Span<byte> chunk)
    {
        for (var index = 0; index < chunk.Length; index++)
            chunk[index] = (byte)(index * 31 + 7);
    }

    private static void VerifyBulkFrame(string lane, ReadOnlySpan<byte> frame, long sequence, int expectedLength, bool verifyBody)
    {
        if (frame.Length != expectedLength || BinaryPrimitives.ReadInt64BigEndian(frame[..8]) != sequence)
            throw new InvalidOperationException($"The {lane} bulk lane delivered a corrupted frame (sequence {sequence}, length {frame.Length}).");
        if (!verifyBody)
            return;
        for (var index = 8; index < frame.Length; index++)
        {
            if (frame[index] != (byte)(index * 31 + 7))
                throw new InvalidOperationException($"The {lane} bulk lane delivered a corrupted frame body (sequence {sequence}).");
        }
    }

    /// <summary>
    /// The client half of every bulk transfer: accepts in-order frames,
    /// verifies each frame's length and sequence (the first and last frames
    /// byte-for-byte), and completes once every expected frame arrived. The
    /// optional ack callback receives batch boundaries and duplicate frames
    /// so a stop-and-wait sender can be unstuck.
    /// </summary>
    private sealed class BulkReceiver
    {
        private readonly string lane;
        private readonly long totalBytes;
        private readonly int chunkBytes;
        private readonly long frameCount;
        private readonly Action<long>? onAck;
        private long nextSequence;
        private long bytesReceived;

        public BulkReceiver(string lane, long totalBytes, int chunkBytes, Action<long>? onAck)
        {
            this.lane = lane;
            this.totalBytes = totalBytes;
            this.chunkBytes = chunkBytes;
            frameCount = BulkFrameCount(totalBytes, chunkBytes);
            this.onAck = onAck;
        }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long FramesReceived { get; private set; }

        public void OnFrame(long sequence, ReadOnlySpan<byte> frame)
        {
            if (nextSequence >= frameCount)
                return;
            if (sequence == nextSequence)
            {
                VerifyBulkFrame(
                    lane,
                    frame,
                    sequence,
                    BulkFrameLength(totalBytes, chunkBytes, sequence),
                    sequence == 0 || sequence == frameCount - 1);
                nextSequence++;
                FramesReceived++;
                bytesReceived += frame.Length;
                if (onAck is not null && (nextSequence % RudpBulkBatchFrames == 0 || nextSequence == frameCount))
                    onAck(nextSequence - 1);
                if (nextSequence == frameCount)
                {
                    if (bytesReceived != totalBytes)
                    {
                        _ = Completion.TrySetException(new InvalidOperationException(
                            $"The {lane} bulk lane delivered {bytesReceived} of {totalBytes} bytes."));
                    }
                    else
                    {
                        _ = Completion.TrySetResult();
                    }
                }
            }
            else if (sequence < nextSequence)
            {
                // A duplicate means the server never saw our ack for it.
                onAck?.Invoke(sequence);
            }
        }

        /// <summary>Re-sends the ack for the last contiguous frame, if any.</summary>
        public bool ResendLastAck()
        {
            if (nextSequence == 0)
                return false;
            onAck?.Invoke(nextSequence - 1);
            return true;
        }

        public void ThrowIfIncomplete()
        {
            if (FramesReceived != frameCount || bytesReceived != totalBytes)
                throw new InvalidOperationException($"The {lane} bulk lane delivered {FramesReceived} of {frameCount} frames.");
        }
    }

    private static WebApplicationBuilder CreateQuietHostBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        // Per-request Information logging is a console write inside every
        // timed region; the RUDP lane has no host, so leaving it on would
        // bill the diagnostics pipeline only to the HTTP/2 lanes.
        builder.Logging.ClearProviders();
        return builder;
    }

    private static async Task<WebApplication> StartGrpcHostAsync()
    {
        var builder = CreateQuietHostBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(grpcOptions =>
        {
            grpcOptions.MaxReceiveMessageSize = MaximumFrameBytes;
            grpcOptions.MaxSendMessageSize = MaximumFrameBytes;
        });
        var app = builder.Build();
        app.MapGrpcService<GrpcEchoService>();
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private static async Task<WebApplication> StartMagicOnionHostAsync()
    {
        var builder = CreateQuietHostBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(grpcOptions =>
        {
            grpcOptions.MaxReceiveMessageSize = MaximumFrameBytes;
            grpcOptions.MaxSendMessageSize = MaximumFrameBytes;
        });
        builder.Services.AddMagicOnion();
        var app = builder.Build();
        app.MapMagicOnionService<TransportEchoService>();
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private static int HostPort(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("The host did not report its bound addresses.");
        var address = addresses.FirstOrDefault(static value => value.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The host did not report a loopback address: {string.Join(", ", addresses)}");
        return new Uri(address).Port;
    }

    /// <summary>
    /// A plain gRPC unary echo service defined without protobuf codegen: the
    /// method uses the identity byte[] marshaller over Grpc.Core.Api, exactly
    /// like the raw methods MagicOnion builds internally. The virtual method
    /// on an abstract base mirrors the shape protobuf codegen emits, which
    /// the ASP.NET Core gRPC service binder resolves by reflection.
    /// </summary>
    [BindServiceMethod(typeof(GrpcEchoBinder), nameof(GrpcEchoBinder.BindService))]
    public abstract class GrpcEchoServiceBase
    {
        /// <summary>Echoes the request payload.</summary>
        public virtual Task<byte[]> Echo(byte[] request, ServerCallContext context) =>
            throw new NotImplementedException();

        /// <summary>Streams a one-way bulk transfer of chunk-sized frames.</summary>
        public virtual Task Bulk(byte[] request, IServerStreamWriter<byte[]> responseStream, ServerCallContext context) =>
            throw new NotImplementedException();
    }

    /// <summary>The concrete echo service mapped by the host.</summary>
    public sealed class GrpcEchoService : GrpcEchoServiceBase
    {
        /// <inheritdoc />
        public override Task<byte[]> Echo(byte[] request, ServerCallContext context) => Task.FromResult(request);

        /// <inheritdoc />
        public override Task Bulk(byte[] request, IServerStreamWriter<byte[]> responseStream, ServerCallContext context)
        {
            var (totalBytes, chunkBytes) = TransportComparisonWorkload.ReadBulkRequest(request, "grpc");
            return TransportComparisonWorkload.SendBulkFramesAsync(
                totalBytes,
                chunkBytes,
                (buffer, take) => responseStream.WriteAsync(take == buffer.Length ? buffer : buffer.AsMemory(0, take).ToArray()));
        }
    }

    /// <summary>Binds <see cref="GrpcEchoServiceBase"/> for MapGrpcService discovery.</summary>
    public static class GrpcEchoBinder
    {
        /// <summary>The unary echo method shared by the server binder and the client invoker.</summary>
        public static readonly Method<byte[], byte[]> EchoMethod = new(
            MethodType.Unary,
            "terrabase.bench.TransportEcho",
            "Echo",
            Marshallers.Create(static (byte[] buffer) => buffer, static (byte[] buffer) => buffer),
            Marshallers.Create(static (byte[] buffer) => buffer, static (byte[] buffer) => buffer));

        /// <summary>The server-streaming bulk method shared by the server binder and the client invoker.</summary>
        public static readonly Method<byte[], byte[]> BulkMethod = new(
            MethodType.ServerStreaming,
            "terrabase.bench.TransportEcho",
            "Bulk",
            Marshallers.Create(static (byte[] buffer) => buffer, static (byte[] buffer) => buffer),
            Marshallers.Create(static (byte[] buffer) => buffer, static (byte[] buffer) => buffer));

        /// <summary>
        /// Registers the service methods for ASP.NET Core gRPC hosting. The
        /// host invokes this with a null service instance: the binder ignores
        /// the handler delegate and reflects the public virtual methods
        /// itself.
        /// </summary>
        public static void BindService(ServiceBinderBase binder, GrpcEchoServiceBase? service)
        {
            binder.AddMethod(
                EchoMethod,
                service is null
                    ? (static (request, context) => Task.FromResult(request))
                    : service.Echo);
            binder.AddMethod(
                BulkMethod,
                service is null
                    ? (static (request, responseStream, context) => Task.CompletedTask)
                    : service.Bulk);
        }
    }

    private sealed class GrpcEchoClient : IAsyncDisposable
    {
        private readonly GrpcChannel channel;
        private readonly CallInvoker invoker;

        public GrpcEchoClient(int port)
        {
            channel = GrpcChannel.ForAddress(
                $"http://127.0.0.1:{port}",
                new GrpcChannelOptions
                {
                    MaxSendMessageSize = MaximumFrameBytes,
                    MaxReceiveMessageSize = MaximumFrameBytes,
                });
            invoker = channel.CreateCallInvoker();
        }

        public Task<byte[]> EchoAsync(byte[] payload) =>
            invoker.AsyncUnaryCall(GrpcEchoBinder.EchoMethod, null, new CallOptions(), payload).ResponseAsync;

        public async Task<BulkResult> BulkAsync(long totalBytes, int chunkBytes)
        {
            using var call = invoker.AsyncServerStreamingCall(
                GrpcEchoBinder.BulkMethod,
                null,
                new CallOptions(),
                BuildBulkRequest(totalBytes, chunkBytes));
            var receiver = new BulkReceiver("grpc", totalBytes, chunkBytes, onAck: null);
            long sequence = 0;
            while (await call.ResponseStream.MoveNext(CancellationToken.None).ConfigureAwait(false))
                receiver.OnFrame(sequence++, call.ResponseStream.Current);
            receiver.ThrowIfIncomplete();
            return new BulkResult((int)receiver.FramesReceived, Retransmits: 0);
        }

        public ValueTask DisposeAsync()
        {
            channel.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MagicOnionEchoClient : IAsyncDisposable
    {
        private readonly GrpcChannel channel;
        private readonly ITransportEchoService service;

        public MagicOnionEchoClient(int port)
        {
            channel = GrpcChannel.ForAddress(
                $"http://127.0.0.1:{port}",
                new GrpcChannelOptions
                {
                    MaxSendMessageSize = MaximumFrameBytes,
                    MaxReceiveMessageSize = MaximumFrameBytes,
                });
            service = MagicOnionClient.Create<ITransportEchoService>(channel);
        }

        public Task<byte[]> EchoAsync(byte[] payload) => service.EchoAsync(payload).ResponseAsync;

        public async Task<BulkResult> BulkAsync(long totalBytes, int chunkBytes)
        {
            var stream = await service.BulkAsync(BuildBulkRequest(totalBytes, chunkBytes)).ConfigureAwait(false);
            var receiver = new BulkReceiver("magic-onion", totalBytes, chunkBytes, onAck: null);
            long sequence = 0;
            while (await stream.ResponseStream.MoveNext(CancellationToken.None).ConfigureAwait(false))
                receiver.OnFrame(sequence++, stream.ResponseStream.Current);
            receiver.ThrowIfIncomplete();
            return new BulkResult((int)receiver.FramesReceived, Retransmits: 0);
        }

        public ValueTask DisposeAsync()
        {
            channel.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The custom reliable-UDP protocol. A frame is a 12-byte little-endian
    /// header (correlation id, payload length) followed by the payload. The
    /// client retransmits the whole frame with exponential backoff until the
    /// response arrives; duplicates are dropped by correlation-id demux.
    /// The bulk phase extends the frame payload with a leading op byte: a
    /// bulk request (op 1, total, chunk) goes out as one frame and the
    /// server answers with a batched stop-and-wait stream of data frames
    /// (header seq | len | payload) acked by op-2 frames.
    /// ponytail: no fragmentation, ordering, or congestion window — one
    /// datagram per request, at most ~64 KiB payloads; add segmentation if
    /// larger frames are ever needed.
    /// </summary>
    private sealed class RudpEchoClient : IAsyncDisposable
    {
        private readonly UdpClient socket;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task receiveLoop;
        private readonly ConcurrentDictionary<ulong, TaskCompletionSource<byte[]>> pending = new();
        private readonly ConcurrentDictionary<ulong, RudpFragmentReassembly> reassembly = new();
        private readonly int retransmitInitialMilliseconds;
        private readonly int retransmitMaxAttempts;
        private long nextCorrelation;
        private long retransmits;
        private long bulkAckResends;
        private volatile BulkReceiver? bulkReceiver;

        public RudpEchoClient(int port, int retransmitInitialMilliseconds, int retransmitMaxAttempts)
        {
            this.retransmitInitialMilliseconds = retransmitInitialMilliseconds;
            this.retransmitMaxAttempts = retransmitMaxAttempts;
            socket = new UdpClient();
            // Concurrent workers burst many datagrams at once; without the
            // larger queue the loopback receive buffer overflows and every
            // dropped frame turns into a 50ms-class retransmit stall.
            socket.Client.ReceiveBufferSize = SocketBufferBytes;
            socket.Client.SendBufferSize = SocketBufferBytes;
            socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        public long Retransmits => Interlocked.Read(ref retransmits);

        public long BulkAckResends => Interlocked.Read(ref bulkAckResends);

        public async Task<byte[]> EchoAsync(byte[] payload)
        {
            if (payload.Length > MaximumDatagramPayloadBytes)
                return await EchoFragmentedAsync(payload).ConfigureAwait(false);

            var correlation = (ulong)Interlocked.Increment(ref nextCorrelation);
            var frame = new byte[RudpHeaderBytes + payload.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(0, 8), correlation);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), (uint)payload.Length);
            payload.CopyTo(frame, RudpHeaderBytes);

            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[correlation] = completion;
            try
            {
                var timeout = TimeSpan.FromMilliseconds(retransmitInitialMilliseconds);
                for (var attempt = 1; ; attempt++)
                {
                    _ = await socket.SendAsync(frame, frame.Length).ConfigureAwait(false);
                    try
                    {
                        return await completion.Task.WaitAsync(timeout, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        if (attempt >= retransmitMaxAttempts)
                            throw new TimeoutException($"The RUDP echo for correlation {correlation} exhausted {retransmitMaxAttempts} attempts.");
                        _ = Interlocked.Increment(ref retransmits);
                        timeout *= 2;
                    }
                }
            }
            finally
            {
                pending.TryRemove(correlation, out _);
            }
        }

        /// <summary>
        /// Sends one payload larger than a single datagram as fixed
        /// 60000-byte fragments ([u64 correlation][u32 total][u16 index]
        /// [fragment]) and retransmits the whole fragment set on timeout.
        /// ponytail: whole-set retransmit without per-fragment acks — exact
        /// loss recovery would need per-fragment accounting; upgrade if
        /// partial loss under load becomes measurable.
        /// </summary>
        private async Task<byte[]> EchoFragmentedAsync(byte[] payload)
        {
            var correlation = (ulong)Interlocked.Increment(ref nextCorrelation);
            var frames = new List<byte[]>((payload.Length + RudpFragmentPayloadBytes - 1) / RudpFragmentPayloadBytes);
            for (var offset = 0; offset < payload.Length; offset += RudpFragmentPayloadBytes)
            {
                var fragmentLength = Math.Min(RudpFragmentPayloadBytes, payload.Length - offset);
                var frame = new byte[RudpFragmentHeaderBytes + fragmentLength];
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(0, 8), correlation);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), (uint)payload.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(RudpHeaderBytes, 2), (ushort)(offset / RudpFragmentPayloadBytes));
                payload.AsSpan(offset, fragmentLength).CopyTo(frame.AsSpan(RudpFragmentHeaderBytes));
                frames.Add(frame);
            }

            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var accumulator = new RudpFragmentReassembly(payload.Length, RudpFragmentPayloadBytes);
            pending[correlation] = completion;
            reassembly[correlation] = accumulator;
            try
            {
                var timeout = TimeSpan.FromMilliseconds(retransmitInitialMilliseconds);
                for (var attempt = 1; ; attempt++)
                {
                    foreach (var frame in frames)
                        _ = await socket.SendAsync(frame, frame.Length).ConfigureAwait(false);
                    try
                    {
                        return await completion.Task.WaitAsync(timeout, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        if (attempt >= retransmitMaxAttempts)
                            throw new TimeoutException($"The RUDP echo for correlation {correlation} exhausted {retransmitMaxAttempts} attempts.");
                        _ = Interlocked.Increment(ref retransmits);
                        timeout *= 2;
                    }
                }
            }
            finally
            {
                pending.TryRemove(correlation, out _);
                reassembly.TryRemove(correlation, out _);
            }
        }

        /// <summary>
        /// Drives the client half of the batched stop-and-wait bulk
        /// protocol: data frames are acked at every 64-frame batch boundary
        /// and at the final frame; when nothing arrives for one second the
        /// last ack is re-sent so a server stuck on a lost ack resumes.
        /// </summary>
        public async Task<BulkResult> BulkAsync(long totalBytes, int chunkBytes)
        {
            var receiver = new BulkReceiver("rudp", totalBytes, chunkBytes, SendBulkAck);
            bulkReceiver = receiver;
            try
            {
                var frame = new byte[RudpHeaderBytes + BulkRequestBytes];
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(0, 8), (ulong)Interlocked.Increment(ref nextCorrelation));
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), BulkRequestBytes);
                BuildBulkRequest(totalBytes, chunkBytes).CopyTo(frame, RudpHeaderBytes);
                _ = await socket.SendAsync(frame, frame.Length).ConfigureAwait(false);
                var stalls = 0;
                while (true)
                {
                    try
                    {
                        await receiver.Completion.Task.WaitAsync(RudpBulkBatchTimeout, cancellation.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                        if (++stalls > RudpBulkMaxStalls)
                            throw new TimeoutException("The RUDP bulk transfer stalled without progress.");
                        if (receiver.ResendLastAck())
                            _ = Interlocked.Increment(ref bulkAckResends);
                    }
                }

                receiver.ThrowIfIncomplete();
                return new BulkResult((int)receiver.FramesReceived, Retransmits: 0);
            }
            finally
            {
                bulkReceiver = null;
            }
        }

        private void SendBulkAck(long lastSequence)
        {
            var frame = new byte[RudpHeaderBytes + RudpBulkAckBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), RudpBulkAckBytes);
            frame[RudpHeaderBytes] = RudpBulkAckOp;
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(RudpHeaderBytes + 1, 8), lastSequence);
            _ = socket.SendAsync(frame, frame.Length);
        }

        private async Task ReceiveLoopAsync()
        {
            while (true)
            {
                UdpReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (received.Buffer.Length < RudpHeaderBytes)
                    continue;
                var correlation = BinaryPrimitives.ReadUInt64LittleEndian(received.Buffer.AsSpan(0, 8));
                var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(received.Buffer.AsSpan(8, 4));
                // A declared total above the single-datagram ceiling is always
                // a fragment of a larger frame; single frames never declare it.
                if (payloadLength > MaximumDatagramPayloadBytes)
                {
                    if (received.Buffer.Length < RudpFragmentHeaderBytes)
                        continue;
                    if (reassembly.TryGetValue(correlation, out var accumulator)
                        && accumulator.Add(
                            BinaryPrimitives.ReadUInt16LittleEndian(received.Buffer.AsSpan(RudpHeaderBytes, 2)),
                            received.Buffer.AsSpan(RudpFragmentHeaderBytes)) is { } completed
                        && pending.TryRemove(correlation, out var done))
                    {
                        reassembly.TryRemove(correlation, out _);
                        _ = done.TrySetResult(completed);
                    }

                    continue;
                }

                if (RudpHeaderBytes + payloadLength != (uint)received.Buffer.Length)
                    continue;
                // During a bulk transfer every inbound frame is a data frame
                // and the header's correlation slot carries its sequence.
                var bulk = bulkReceiver;
                if (bulk is not null)
                {
                    bulk.OnFrame((long)correlation, received.Buffer.AsSpan(RudpHeaderBytes));
                    continue;
                }

                if (!pending.TryRemove(correlation, out var completion))
                    continue;
                _ = completion.TrySetResult(received.Buffer.AsSpan(RudpHeaderBytes).ToArray());
            }

            // A stopped receive loop would otherwise leave every in-flight
            // waiter stalled until its retransmit budget is exhausted.
            foreach (var correlation in pending.Keys)
            {
                if (pending.TryRemove(correlation, out var stalled))
                    _ = stalled.TrySetException(new InvalidOperationException("The RUDP receive loop stopped."));
            }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            socket.Dispose();
            await receiveLoop.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Reassembles one fragmented frame. Fragments are fixed
    /// <paramref name="sliceBytes"/>-byte slices except the last, so
    /// completion is detected by accumulated byte count; out-of-range or
    /// duplicate fragments are ignored.
    /// </summary>
    private sealed class RudpFragmentReassembly
    {
        private readonly byte[] buffer;
        private readonly bool[] received;
        private readonly int sliceBytes;
        private readonly object gate = new();
        private long remaining;

        public RudpFragmentReassembly(int totalLength, int sliceBytes)
        {
            buffer = new byte[totalLength];
            received = new bool[(totalLength + sliceBytes - 1) / sliceBytes];
            this.sliceBytes = sliceBytes;
            remaining = totalLength;
        }

        /// <summary>Returns the completed frame, or null while fragments are missing.</summary>
        public byte[]? Add(int index, ReadOnlySpan<byte> fragment)
        {
            lock (gate)
            {
                if (index >= received.Length
                    || received[index]
                    || (long)index * sliceBytes + fragment.Length > buffer.Length)
                    return null;
                received[index] = true;
                fragment.CopyTo(buffer.AsSpan(index * sliceBytes));
                remaining -= fragment.Length;
                if (remaining > 0)
                    return null;
                return buffer;
            }
        }
    }

    /// <summary>
    /// The UDP listener that answers each echo frame with the same frame and
    /// serves bulk requests with a batched stop-and-wait stream: batches of
    /// 64 data frames, then one second waiting for the batch ack before the
    /// batch is re-sent. Bulk control frames are distinguished from echo
    /// traffic by their exact payload length and leading op byte (the
    /// benchmark's echo payloads are never 13 or 9 bytes).
    /// </summary>
    private sealed class RudpEchoServer : IDisposable
    {
        private readonly UdpClient socket;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task receiveLoop;
        private readonly ConcurrentDictionary<ulong, RudpFragmentReassembly> reassembly = new();
        private long bulkRetransmits;
        private volatile RudpBulkSession? bulkSession;
        // ponytail: partial reassemblies from clients that vanish mid-frame
        // are never evicted; a TTL keyed by first-fragment time replaces this
        // if long-lived servers ever reuse this protocol.

        public RudpEchoServer()
        {
            socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Client.ReceiveBufferSize = SocketBufferBytes;
            socket.Client.SendBufferSize = SocketBufferBytes;
            Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        public int Port { get; }

        public long BulkRetransmits => Interlocked.Read(ref bulkRetransmits);

        private async Task ReceiveLoopAsync()
        {
            while (true)
            {
                UdpReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (received.Buffer.Length < RudpHeaderBytes)
                    continue;
                var correlation = BinaryPrimitives.ReadUInt64LittleEndian(received.Buffer.AsSpan(0, 8));
                var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(received.Buffer.AsSpan(8, 4));
                // Fragments declare a total above the single-datagram ceiling;
                // they must be reassembled before any single-frame handling.
                if (payloadLength > MaximumDatagramPayloadBytes)
                {
                    if (received.Buffer.Length >= RudpFragmentHeaderBytes)
                        await HandleEchoFragmentAsync(received.Buffer, received.RemoteEndPoint!).ConfigureAwait(false);
                    continue;
                }
                if (RudpHeaderBytes + payloadLength != (uint)received.Buffer.Length)
                    continue;
                var payload = received.Buffer.AsSpan(RudpHeaderBytes);
                if (payloadLength == BulkRequestBytes && payload[0] == BulkRequestOp)
                {
                    StartBulkSession(payload, received.RemoteEndPoint!);
                    continue;
                }

                if (payloadLength == RudpBulkAckBytes && payload[0] == RudpBulkAckOp)
                {
                    bulkSession?.OnAck(BinaryPrimitives.ReadInt64BigEndian(payload.Slice(1, 8)));
                    continue;
                }

                try
                {
                    _ = await socket.SendAsync(received.Buffer, received.Buffer.Length, received.RemoteEndPoint).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Reassembles one inbound fragment; once the frame is complete it is
        /// echoed back, fragmented the same way when it exceeds one datagram.
        /// </summary>
        private async Task HandleEchoFragmentAsync(byte[] datagram, IPEndPoint client)
        {
            var correlation = BinaryPrimitives.ReadUInt64LittleEndian(datagram.AsSpan(0, 8));
            var totalLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(datagram.AsSpan(8, 4));
            var index = BinaryPrimitives.ReadUInt16LittleEndian(datagram.AsSpan(RudpHeaderBytes, 2));
            if (totalLength <= MaximumDatagramPayloadBytes || totalLength > MaximumFrameBytes)
                return;
            var accumulator = reassembly.GetOrAdd(correlation, _ => new RudpFragmentReassembly(totalLength, RudpFragmentPayloadBytes));
            if (accumulator.Add(index, datagram.AsSpan(RudpFragmentHeaderBytes)) is not { } completed)
                return;
            reassembly.TryRemove(correlation, out _);

            for (var offset = 0; offset < completed.Length; offset += RudpFragmentPayloadBytes)
            {
                var fragmentLength = Math.Min(RudpFragmentPayloadBytes, completed.Length - offset);
                var frame = new byte[RudpFragmentHeaderBytes + fragmentLength];
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(0, 8), correlation);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), (uint)completed.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(RudpHeaderBytes, 2), (ushort)(offset / RudpFragmentPayloadBytes));
                completed.AsSpan(offset, fragmentLength).CopyTo(frame.AsSpan(RudpFragmentHeaderBytes));
                await SendReplyAsync(frame, client).ConfigureAwait(false);
            }
        }

        private async Task SendReplyAsync(byte[] frame, IPEndPoint client)
        {
            try
            {
                _ = await socket.SendAsync(frame, frame.Length, client).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // A lost client mid-burst leaves the send to fail; the
                // receive loop keeps serving other traffic.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void StartBulkSession(ReadOnlySpan<byte> payload, IPEndPoint client)
        {
            long totalBytes;
            int chunkBytes;
            try
            {
                (totalBytes, chunkBytes) = ReadBulkRequest(payload, "rudp");
            }
            catch (InvalidOperationException)
            {
                return;
            }

            // A new request supersedes any session still waiting on a lost
            // ack (loopback delivery keeps this from happening in practice).
            bulkSession?.Cancel();
            var session = new RudpBulkSession(
                socket,
                client,
                totalBytes,
                chunkBytes,
                () => Interlocked.Increment(ref bulkRetransmits),
                EndBulkSession);
            bulkSession = session;
            _ = session.RunAsync();
        }

        private void EndBulkSession(RudpBulkSession session)
        {
            if (ReferenceEquals(bulkSession, session))
                bulkSession = null;
        }

        /// <summary>
        /// The server half of the RUDP bulk protocol: blasts data frames in
        /// batches of 64 and waits for the ack of each batch, re-sending the
        /// whole batch when no ack arrives within one second. Reliability is
        /// batch-granular stop-and-wait.
        /// </summary>
        private sealed class RudpBulkSession
        {
            private readonly UdpClient socket;
            private readonly IPEndPoint client;
            private readonly long totalBytes;
            private readonly int chunkBytes;
            private readonly long frameCount;
            private readonly Action onRetransmit;
            private readonly Action<RudpBulkSession> onFinished;
            private volatile BulkAckGate? gate;
            private volatile bool cancelled;

            public RudpBulkSession(
                UdpClient socket,
                IPEndPoint client,
                long totalBytes,
                int chunkBytes,
                Action onRetransmit,
                Action<RudpBulkSession> onFinished)
            {
                this.socket = socket;
                this.client = client;
                this.totalBytes = totalBytes;
                this.chunkBytes = chunkBytes;
                frameCount = BulkFrameCount(totalBytes, chunkBytes);
                this.onRetransmit = onRetransmit;
                this.onFinished = onFinished;
            }

            public void OnAck(long lastSequence)
            {
                var current = gate;
                if (current is not null && lastSequence >= current.NeededSequence)
                    _ = current.Signal.TrySetResult(true);
            }

            public void Cancel()
            {
                cancelled = true;
                gate?.Signal.TrySetResult(false);
            }

            public async Task RunAsync()
            {
                try
                {
                    var frame = new byte[RudpHeaderBytes + chunkBytes];
                    FillBulkChunk(frame.AsSpan(RudpHeaderBytes));
                    long sequence = 0;
                    while (sequence < frameCount && !cancelled)
                    {
                        var batchEnd = (int)Math.Min(sequence + RudpBulkBatchFrames, frameCount);
                        var current = new BulkAckGate(
                            batchEnd - 1,
                            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                        gate = current;
                        while (!cancelled)
                        {
                            for (var index = sequence; index < batchEnd; index++)
                            {
                                var take = BulkFrameLength(totalBytes, chunkBytes, index);
                                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(0, 8), (ulong)index);
                                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), (uint)take);
                                BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(RudpHeaderBytes, 8), index);
                                _ = await socket.SendAsync(frame, RudpHeaderBytes + take, client).ConfigureAwait(false);
                            }

                            try
                            {
                                await current.Signal.Task.WaitAsync(RudpBulkBatchTimeout).ConfigureAwait(false);
                                break;
                            }
                            catch (TimeoutException)
                            {
                                onRetransmit();
                            }
                        }

                        sequence = batchEnd;
                    }
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                finally
                {
                    onFinished(this);
                }
            }
        }

        private sealed record BulkAckGate(long NeededSequence, TaskCompletionSource<bool> Signal);

        public void Dispose()
        {
            cancellation.Cancel();
            socket.Dispose();
            _ = receiveLoop.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// The QUIC echo server: accepts connections and, on each, any number of
    /// inbound bidirectional streams. Each stream carries one
    /// length-prefixed frame that is echoed back, or one bulk request whose
    /// chunked frames the server streams in reply. The certificate is a
    /// throwaway ECDSA key pair written to a private temporary directory.
    /// </summary>
    [SuppressMessage(
        "Interoperability",
        "CA1416:ValidatePlatformCompatibility",
        Justification = "The transport comparison targets Linux benchmark hosts; the QUIC lanes require libmsquic at runtime.")]
    private sealed class QuicEchoServer : IAsyncDisposable
    {
        private readonly QuicListener listener;
        private readonly X509Certificate2 certificate;
        private readonly string certificateDirectory;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task acceptLoop;

        private QuicEchoServer(QuicListener listener, X509Certificate2 certificate, string certificateDirectory)
        {
            this.listener = listener;
            this.certificate = certificate;
            this.certificateDirectory = certificateDirectory;
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        /// <summary>The loopback endpoint clients connect to.</summary>
        public IPEndPoint EndPoint => (IPEndPoint)listener.LocalEndPoint;

        /// <summary>Starts a loopback listener with a throwaway certificate.</summary>
        public static async Task<QuicEchoServer> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"terrabase-quic-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var certificatePath = Path.Combine(directory, "cert.pem");
            var keyPath = Path.Combine(directory, "key.pem");
            var certificate = WriteCertificate(certificatePath, keyPath);
            var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol> { QuicApplicationProtocol },
                ListenBacklog = 8,
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                    },
                    IdleTimeout = QuicIdleTimeout,
                    DefaultCloseErrorCode = 0,
                    DefaultStreamErrorCode = 0,
                }),
            }).ConfigureAwait(false);
            return new QuicEchoServer(listener, certificate, directory);
        }

        /// <summary>Writes a self-signed ECDSA P-256 certificate and key as PEM files.</summary>
        private static X509Certificate2 WriteCertificate(string certificatePath, string keyPath)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                new X500DistinguishedName("CN=terrabase-bench"),
                key,
                HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(12));
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            return certificate;
        }

        private async Task AcceptLoopAsync()
        {
            var handlers = new List<Task>();
            while (true)
            {
                QuicConnection connection;
                try
                {
                    connection = await listener.AcceptConnectionAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (QuicException)
                {
                    break;
                }

                handlers.Add(Task.Run(() => HandleConnectionAsync(connection)));
            }

            await Task.WhenAll(handlers).ConfigureAwait(false);
        }

        private async Task HandleConnectionAsync(QuicConnection connection)
        {
            var streams = new List<Task>();
            while (true)
            {
                QuicStream stream;
                try
                {
                    stream = await connection.AcceptInboundStreamAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (QuicException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                streams.Add(Task.Run(() => HandleStreamAsync(stream)));
            }

            await Task.WhenAll(streams).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        private static async Task HandleStreamAsync(QuicStream stream)
        {
            try
            {
                var header = new byte[4];
                await QuicReadExactlyAsync(stream, header).ConfigureAwait(false);
                var bodyLength = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (bodyLength > MaximumFrameBytes)
                    throw new InvalidOperationException($"The QUIC echo server received an invalid frame length ({bodyLength}).");
                var body = new byte[bodyLength];
                await QuicReadExactlyAsync(stream, body).ConfigureAwait(false);
                if (bodyLength == BulkRequestBytes && body[0] == BulkRequestOp)
                {
                    var (totalBytes, chunkBytes) = ReadBulkRequest(body, "quic");
                    var prefix = new byte[4];
                    await SendBulkFramesAsync(totalBytes, chunkBytes, async (frame, take) =>
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)take);
                        await stream.WriteAsync(prefix).ConfigureAwait(false);
                        await stream.WriteAsync(frame.AsMemory(0, take)).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    stream.CompleteWrites();
                    return;
                }

                var reply = new byte[4 + body.Length];
                BinaryPrimitives.WriteUInt32BigEndian(reply, (uint)body.Length);
                body.CopyTo(reply, 4);
                await stream.WriteAsync(reply).ConfigureAwait(false);
                stream.CompleteWrites();
            }
            catch (Exception exception) when (exception is OperationCanceledException
                or QuicException
                or IOException
                or ObjectDisposedException
                or InvalidOperationException)
            {
                // A malformed or lost stream ends its own handling; the
                // connection keeps serving other streams.
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            await listener.DisposeAsync().ConfigureAwait(false);
            await acceptLoop.ConfigureAwait(false);
            cancellation.Dispose();
            certificate.Dispose();
            try
            {
                Directory.Delete(certificateDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The throwaway key material is best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// The QUIC echo client: one long-lived connection; each operation opens
    /// its own bidirectional stream, sends one length-prefixed frame, and
    /// reads the echoed frame back.
    /// </summary>
    [SuppressMessage(
        "Interoperability",
        "CA1416:ValidatePlatformCompatibility",
        Justification = "The transport comparison targets Linux benchmark hosts; the QUIC lanes require libmsquic at runtime.")]
    [SuppressMessage(
        "Security",
        "CA5359:DoNotDisableServerCertificateValidation",
        Justification = "The benchmark connects to its own throwaway self-signed certificate over loopback; there is no identity to validate.")]
    private sealed class QuicEchoClient : IAsyncDisposable
    {
        private readonly QuicConnection connection;

        private QuicEchoClient(QuicConnection connection) => this.connection = connection;

        /// <summary>Connects to the echo server over TLS 1.3, trusting its throwaway certificate.</summary>
        public static async Task<QuicEchoClient> ConnectAsync(QuicEchoServer server)
        {
            var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = server.EndPoint,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    ApplicationProtocols = new List<SslApplicationProtocol> { QuicApplicationProtocol },
                },
                IdleTimeout = QuicIdleTimeout,
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
            }).ConfigureAwait(false);
            return new QuicEchoClient(connection);
        }

        public Task<byte[]> EchoAsync(byte[] payload) =>
            EchoCoreAsync(payload).WaitAsync(QuicEchoTimeout);

        private async Task<byte[]> EchoCoreAsync(byte[] payload)
        {
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional).ConfigureAwait(false);
            var frame = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
            payload.CopyTo(frame, 4);
            await stream.WriteAsync(frame).ConfigureAwait(false);
            // Half-close: the request is complete, the reply is still read.
            stream.CompleteWrites();
            var header = new byte[4];
            await QuicReadExactlyAsync(stream, header).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length != (uint)payload.Length)
                throw new InvalidOperationException($"The QUIC lane returned a corrupted echo length ({length}).");
            var body = new byte[length];
            await QuicReadExactlyAsync(stream, body).ConfigureAwait(false);
            return body;
        }

        /// <summary>
        /// Sends one bulk request on its own stream and reads the streamed
        /// reply frames until the transfer completes.
        /// </summary>
        public async Task<BulkResult> BulkAsync(long totalBytes, int chunkBytes)
        {
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional).ConfigureAwait(false);
            var request = new byte[4 + BulkRequestBytes];
            BinaryPrimitives.WriteUInt32BigEndian(request, (uint)BulkRequestBytes);
            BuildBulkRequest(totalBytes, chunkBytes).CopyTo(request, 4);
            await stream.WriteAsync(request).ConfigureAwait(false);
            stream.CompleteWrites();
            var receiver = new BulkReceiver("quic", totalBytes, chunkBytes, onAck: null);
            var header = new byte[4];
            while (!receiver.Completion.Task.IsCompleted)
            {
                await QuicReadExactlyAsync(stream, header).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length < 8 || length > MaximumFrameBytes)
                    throw new InvalidOperationException($"The QUIC bulk lane received an invalid frame length ({length}).");
                var frame = new byte[length];
                await QuicReadExactlyAsync(stream, frame).ConfigureAwait(false);
                receiver.OnFrame(BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(0, 8)), frame.AsSpan(8));
            }

            receiver.ThrowIfIncomplete();
            return new BulkResult((int)receiver.FramesReceived, Retransmits: null);
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that System.Net.Quic can complete a TLS 1.3 handshake and
    /// one echo on this host before the quic lanes measure anything; returns
    /// null when healthy, otherwise the failure to report per row.
    /// </summary>
    private static async Task<string?> QuicPreflightAsync()
    {
        if (!QuicListener.IsSupported)
            return "System.Net.Quic is unsupported (libmsquic missing)";
        try
        {
            await using var server = await QuicEchoServer.StartAsync().ConfigureAwait(false);
            await using var client = await QuicEchoClient.ConnectAsync(server).ConfigureAwait(false);
            var probe = new byte[64];
            var echoed = await client.EchoAsync(probe).ConfigureAwait(false);
            return echoed.Length == probe.Length
                ? null
                : $"QUIC echo returned {echoed.Length} of {probe.Length} bytes";
        }
        catch (Exception exception)
        {
            return $"QUIC preflight failed: {exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <summary>Fills the buffer exactly from the stream or throws on early end.</summary>
    [SuppressMessage(
        "Interoperability",
        "CA1416:ValidatePlatformCompatibility",
        Justification = "The transport comparison targets Linux benchmark hosts; the QUIC lanes require libmsquic at runtime.")]
    private static async Task QuicReadExactlyAsync(QuicStream stream, byte[] buffer)
    {
        var received = 0;
        while (received < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(received)).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            received += read;
        }
    }

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return ordered[lower];
        var weight = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * weight;
    }

    private static async Task WriteReceiptAsync(
        TransportComparisonReceipt receipt,
        string outputDirectory)
    {
        var receiptPath = Path.Combine(outputDirectory, "receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
        var summaryPath = Path.Combine(outputDirectory, "summary.md");
        await File.WriteAllTextAsync(summaryPath, CreateMarkdown(receipt, receiptPath)).ConfigureAwait(false);
        Console.WriteLine($"Transport comparison receipt: {receiptPath}");
        Console.WriteLine($"Transport comparison summary: {summaryPath}");
    }

    private static string CreateMarkdown(TransportComparisonReceipt receipt, string receiptPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# TerraBase transport comparison: gRPC vs MagicOnion vs custom RUDP vs KCP");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Status: **{receipt.Status}**  ");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Receipt: `{receiptPath}`  ");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Command: `{receipt.Command}`");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Commit: `{receipt.Environment.GitCommit ?? "unavailable"}` (dirty: `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`)");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Benchmark artifact SHA-256: `{receipt.Environment.ArtifactSha256 ?? "unavailable"}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {receipt.Environment.FrameworkDescription}; {receipt.Environment.Runtime}; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}; build `{receipt.Environment.BuildConfiguration}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"CPU: {receipt.Environment.CpuModel}; {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes:N0} bytes");
        builder.AppendLine(CultureInfo.InvariantCulture, $"GC: server `{receipt.Environment.ServerGc}`, latency mode `{receipt.Environment.GcLatencyMode}`");
        builder.AppendLine();
        builder.AppendLine("Lanes: plain gRPC unary over cleartext HTTP/2 (identity byte[] marshaller, no protobuf codegen); MagicOnion 7 unary over the same stack (adds its MessagePack marshalling and RPC layer); a custom reliable-UDP protocol (12-byte correlation header over UDP datagrams, client retransmit with exponential backoff); QUIC (System.Net.Quic over libmsquic): one bidirectional stream per operation multiplexed over one TLS 1.3 connection — the only lane whose wire is always encrypted. The grpc+lz4, rudp+lz4 and quic+lz4 lanes reuse those echo paths unchanged but send a synthetic structured-data corpus compressed with LZ4 at the client, then decompress and verify the echo against the original corpus; their LZ4 ratio (compressed/original bytes) is reported per row. All lanes echo the same payload over loopback sockets in one process; each table value is the median across samples.");
        builder.AppendLine();
        builder.AppendLine("## Sequential round-trip latency");
        builder.AppendLine();
        builder.AppendLine("| Lane | Payload (bytes) | p50 (us) | p95 (us) | p99 (us) | Mean (us) | LZ4 ratio |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in MedianRows(receipt, "latency"))
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {row.Lane} | {row.PayloadBytes} | {Format(row.P50)} | {Format(row.P95)} | {Format(row.P99)} | {Format(row.Mean)} | {FormatRatio(row.CompressionRatio)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Closed-loop throughput");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Concurrent workers: {receipt.Configuration.ThroughputWorkers}; operations per lane and payload: {receipt.Configuration.ThroughputOperations}.");
        builder.AppendLine();
        builder.AppendLine("| Lane | Payload (bytes) | Ops/s | Payload MB/s | Retransmits | LZ4 ratio |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var row in MedianRows(receipt, "throughput"))
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {row.Lane} | {row.PayloadBytes} | {Format(row.OperationsPerSecond, "F0")} | {Format(row.PayloadMegabytesPerSecond, "F1")} | {row.Retransmits?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | {FormatRatio(row.CompressionRatio)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Raw one-way bulk throughput");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Server-to-client one-way transfer of {receipt.Configuration.BulkBytes:N0} bytes per lane in {receipt.Configuration.BulkChunkBytes:N0}-byte frames (RUDP frames capped at {RudpBulkMaxFrameBytes:N0} bytes); one 1 MiB warm pass per lane is unmeasured.");
        builder.AppendLine();
        builder.AppendLine("| Lane | Chunk (bytes) | Frames | MB/s | Retransmits |");
        builder.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var row in MedianRows(receipt, "bulk"))
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {row.Lane} | {row.PayloadBytes} | {row.Operations?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | {Format(row.PayloadMegabytesPerSecond, "F1")} | {row.Retransmits?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} |");
        }

        builder.AppendLine();
        builder.AppendLine("Bulk caveats:");
        builder.AppendLine();
        builder.AppendLine("- Direction is server to client: each lane times one bulk request from send to last verified byte; nothing echoes back, and connect plus the warm pass are excluded.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- The RUDP bulk protocol is batched stop-and-wait: the server sends {RudpBulkBatchFrames}-frame batches and waits up to one second for a batch ack, then resends the batch; the client re-sends its ack when nothing arrives for one second. Bulk frames are capped at {RudpBulkMaxFrameBytes:N0} bytes (larger requested chunks are clamped, so the lane's chunk column shows the effective frame size), and its Retransmits column counts server batch resends plus client ack re-sends.");
        builder.AppendLine("- The KCP lane runs at library defaults; its send window and update interval bound the transfer rate (measured at roughly 4 MB/s with 64 KiB frames, so the lane runs the full configured total uncapped), and KCP retransmission stays internal to the library (Retransmits n/a).");
        builder.AppendLine("- The gRPC and MagicOnion lanes bulk over one HTTP/2 server-streaming call each, the same single connection their RPC phases use.");
        builder.AppendLine();
        builder.AppendLine("Unsupported claims and limits:");
        builder.AppendLine();
        builder.AppendLine("- Loopback in one process: this measures the RPC stack overhead (framing, marshalling, dispatch), not physical network, WAN latency, or TLS; production peer traffic adds mTLS equally to both HTTP/2 lanes.");
        builder.AppendLine("- The RUDP lane is stop-and-wait per request with client-side retransmit (initial timeout 50ms, exponential backoff); payloads above one datagram are retransmitted as whole fragment sets, with 60000-byte fragments. No ordering guarantees or congestion control. Both RUDP sockets use 4 MiB buffers; retransmits in the receipt are loopback receive-queue overflows under concurrent bursts, recovered by the protocol.");
        builder.AppendLine("- The QUIC lane always encrypts (TLS 1.3 with a throwaway ECDSA certificate): it pays crypto on every byte while the other lanes run cleartext, and production peer traffic would add mTLS to them equally. Loss recovery is internal to QUIC and not observable, so the Retransmits column reports n/a; the lane requires libmsquic at runtime.");
        builder.AppendLine("- The +lz4 lanes differ from the uncompressed lanes by design: wire bytes are the compressed frame, not the corpus, so Payload MB/s counts corpus bytes that never crossed the wire uncompressed; the LZ4 codec CPU is client-side and inside every measured window; and the servers never decompress, they echo the compressed bytes verbatim. The corpus compresses below the RUDP datagram cap by construction and the harness fails loudly if a size ever stops fitting. The 64-byte corpora barely compress, so LZ4 frame overhead can leave that ratio at or slightly above 1.0.");
        builder.AppendLine("- Throughput workers share one connection (gRPC/MagicOnion HTTP/2 multiplexing), one socket (RUDP), or one stream (KCP); multi-connection scaling is not measured.");
        builder.AppendLine("- Server processing is a pure echo, so differences isolate transport cost, not application or storage work.");
        builder.AppendLine("- Workstation GC and the machine's background load shape the tails; samples mediate but do not eliminate that noise.");
        return builder.ToString();
    }

    private static string Format(double? value, string format = "F1") =>
        value is { } number ? number.ToString(format, CultureInfo.InvariantCulture) : "unsupported";

    private static string FormatRatio(double? ratio) =>
        ratio?.ToString("F2", CultureInfo.InvariantCulture) ?? "n/a";

    private sealed record MedianRow(
        string Lane,
        int PayloadBytes,
        double? P50,
        double? P95,
        double? P99,
        double? Mean,
        double? OperationsPerSecond,
        double? PayloadMegabytesPerSecond,
        long? Retransmits,
        long? Operations,
        double? CompressionRatio);

    private static readonly string[] LaneOrder = { "grpc", "grpc+lz4", "magic-onion", "rudp", "quic", "rudp+lz4", "quic+lz4" };

    private static List<MedianRow> MedianRows(TransportComparisonReceipt receipt, string phase)
    {
        var rows = new List<MedianRow>();
        var lanes = receipt.Samples
            .SelectMany(static sample => sample.Measurements)
            .Where(measurement => measurement.Phase == phase)
            .GroupBy(measurement => (measurement.Lane, measurement.PayloadBytes))
            .OrderBy(group => group.Key.PayloadBytes)
            .ThenBy(group => Array.IndexOf(LaneOrder, group.Key.Lane));
        foreach (var group in lanes)
        {
            var measurements = group.ToList();
            rows.Add(new MedianRow(
                group.Key.Lane,
                group.Key.PayloadBytes,
                Median(measurements.Select(static measurement => measurement.P50Microseconds)),
                Median(measurements.Select(static measurement => measurement.P95Microseconds)),
                Median(measurements.Select(static measurement => measurement.P99Microseconds)),
                Median(measurements.Select(static measurement => measurement.MeanMicroseconds)),
                Median(measurements.Select(static measurement => measurement.OperationsPerSecond)),
                Median(measurements.Select(static measurement => measurement.PayloadMegabytesPerSecond)),
                Median(measurements.Select(static measurement => measurement.Retransmits)),
                Operations: Median(measurements.Select(static measurement => (long?)measurement.Operations)),
                Median(measurements.Select(static measurement => measurement.CompressionRatio))));
        }

        return rows;
    }

    private static double? Median(IEnumerable<double?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).OrderBy(static value => value).ToArray();
        return present.Length == 0
            ? null
            : present.Length % 2 == 1
                ? present[present.Length / 2]
                : (present[(present.Length / 2) - 1] + present[present.Length / 2]) / 2;
    }

    private static long? Median(IEnumerable<long?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).OrderBy(static value => value).ToArray();
        return present.Length == 0
            ? null
            : present.Length % 2 == 1
                ? present[present.Length / 2]
                : (present[(present.Length / 2) - 1] + present[present.Length / 2]) / 2;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))
                return argument[($"--{name}=".Length)..];
            if (argument.Equals($"--{name}", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
                return args[index + 1];
        }

        return null;
    }

    private static EnvironmentIdentity BuildEnvironmentIdentity()
    {
        var artifactPath = Assembly.GetExecutingAssembly().Location;
        var terraBasePath = typeof(TerraBaseHost).Assembly.Location;
        var gitRoot = FindGitRoot();
        return new EnvironmentIdentity(
            DateTimeOffset.UtcNow,
            Environment.Version.ToString(),
            RuntimeInformation.FrameworkDescription,
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ReadCpuModel(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD"),
            gitRoot is not null && !string.IsNullOrWhiteSpace(RunGit(gitRoot, "status", "--porcelain")),
            string.IsNullOrWhiteSpace(artifactPath) ? null : artifactPath,
            HashFile(artifactPath),
            string.IsNullOrWhiteSpace(terraBasePath) ? null : terraBasePath,
            HashFile(terraBasePath));
    }

    private static string? FindGitRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }

        return null;
    }

    private static string? RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            process.WaitForExit();
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? HashFile(string? path) =>
        path is { Length: > 0 } && File.Exists(path)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            : null;

    private static string ReadCpuModel()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                var line = File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(static value => value.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                if (line is not null)
                {
                    var separator = line.IndexOf(':');
                    return separator >= 0 ? line[(separator + 1)..].Trim() : line.Trim();
                }
            }
        }
        catch (IOException)
        {
            // The host model is optional metadata.
        }

        return "unknown";
    }

    private sealed class TransportComparisonOptions
    {
        public int Samples { get; private init; } = 3;
        public int WarmupOperations { get; private init; } = 200;
        public int LatencyOperations { get; private init; } = 2_000;
        public int ThroughputOperations { get; private init; } = 4_000;
        public int ThroughputWorkers { get; private init; } = 16;
        public int RetransmitInitialMilliseconds { get; private init; } = 50;
        public int RetransmitMaxAttempts { get; private init; } = 8;
        public IReadOnlyList<int> PayloadBytes { get; private init; } = new[] { 64, 1_024, 16_384, 61_440 };
        public int BulkBytes { get; private init; } = 134_217_728;
        public int BulkChunkBytes { get; private init; } = 65_536;
        public string OutputDirectory { get; private init; } = Path.Combine("bench", "receipts", "transport-comparison");
        public bool ShowHelp { get; private init; }

        public static TransportComparisonOptions Parse(string[] args)
        {
            if (args.Any(argument => argument is "--help" or "-h"))
                return new TransportComparisonOptions { ShowHelp = true };

            return new TransportComparisonOptions
            {
                Samples = ParseInteger(args, "samples", 1, 100, 3),
                WarmupOperations = ParseInteger(args, "warmup-operations", 1, 100_000, 200),
                LatencyOperations = ParseInteger(args, "latency-operations", 2, 1_000_000, 2_000),
                ThroughputOperations = ParseInteger(args, "throughput-operations", 2, 1_000_000, 4_000),
                ThroughputWorkers = ParseInteger(args, "throughput-workers", 1, 512, 16),
                RetransmitInitialMilliseconds = ParseInteger(args, "retransmit-initial-ms", 1, 60_000, 50),
                RetransmitMaxAttempts = ParseInteger(args, "retransmit-max-attempts", 1, 64, 8),
                PayloadBytes = ParsePayloadBytes(ReadOption(args, "payload-bytes")),
                BulkBytes = ParseInteger(args, "bulk-bytes", 1_048_576, 1_073_741_824, 134_217_728),
                BulkChunkBytes = ParseInteger(args, "bulk-chunk-bytes", 1_024, 262_144, 65_536),
                OutputDirectory = ReadOption(args, "output") ?? Path.Combine(
                    "bench",
                    "receipts",
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-transport-comparison"),
            };
        }

        public static void PrintHelp()
        {
            Console.WriteLine("--workload transport-comparison");
            Console.WriteLine("  --samples N                 independent samples (default 3)");
            Console.WriteLine("  --warmup-operations N       unmeasured warmup calls per lane and payload (default 200)");
            Console.WriteLine("  --latency-operations N      sequential round trips per lane and payload (default 2000)");
            Console.WriteLine("  --throughput-operations N   total closed-loop operations per lane and payload (default 4000)");
            Console.WriteLine("  --throughput-workers N      concurrent closed-loop workers (default 16)");
            Console.WriteLine("  --payload-bytes LIST        comma-separated payload sizes, 1..61440 each (default 64,1024,16384,61440)");
            Console.WriteLine("  --bulk-bytes N              total one-way bulk bytes per lane, 1048576..1073741824 (default 134217728)");
            Console.WriteLine("  --bulk-chunk-bytes N        bulk frame payload bytes, 1024..262144 (default 65536)");
            Console.WriteLine("  --retransmit-initial-ms N   RUDP initial retransmit timeout (default 50)");
            Console.WriteLine("  --retransmit-max-attempts N RUDP retransmit attempts before failing (default 8)");
            Console.WriteLine("  --output PATH               receipt directory");
        }

        private static int ParseInteger(string[] args, string name, int minimum, int maximum, int defaultValue)
        {
            var value = ReadOption(args, name);
            if (value is null)
                return defaultValue;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < minimum
                || parsed > maximum)
            {
                throw new ArgumentException($"--{name} must be between {minimum} and {maximum}.", nameof(args));
            }

            return parsed;
        }

        private static int[] ParsePayloadBytes(string? value)
        {
            if (value is null)
                return new[] { 64, 1_024, 16_384, 61_440 };
            var sizes = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (sizes.Length == 0 || sizes.Length > 8)
                throw new ArgumentException("--payload-bytes must list between 1 and 8 sizes.", nameof(value));
            var parsedSizes = new int[sizes.Length];
            for (var index = 0; index < sizes.Length; index++)
            {
                if (!int.TryParse(sizes[index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 1
                    || parsed > MaximumFrameBytes)
                {
                    throw new ArgumentException(
                        $"--payload-bytes entries must be between 1 and {MaximumFrameBytes}.",
                        nameof(value));
                }

                parsedSizes[index] = parsed;
            }

            return parsedSizes;
        }
    }

    private sealed record TransportEchoMeasurement(
        string Lane,
        string Phase,
        int PayloadBytes,
        int Operations,
        int Workers,
        double? P50Microseconds,
        double? P95Microseconds,
        double? P99Microseconds,
        double? MeanMicroseconds,
        double? OperationsPerSecond,
        double? PayloadMegabytesPerSecond,
        long? Retransmits,
        double? CompressionRatio);

    private sealed record TransportComparisonSample(
        int Sample,
        IReadOnlyList<TransportEchoMeasurement> Measurements);

    private sealed record TransportComparisonReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        TransportComparisonOptions Configuration,
        IReadOnlyList<TransportComparisonSample> Samples,
        string RawSamplesPath,
        string Command,
        string ReceiptDirectory,
        EnvironmentIdentity Environment,
        string? Error);

    private sealed record EnvironmentIdentity(
        DateTimeOffset StartedAtUtc,
        string Runtime,
        string FrameworkDescription,
        string BuildConfiguration,
        string OperatingSystem,
        string Architecture,
        string CpuModel,
        int ProcessorCount,
        long AvailableMemoryBytes,
        bool ServerGc,
        string GcLatencyMode,
        string? GitCommit,
        bool? GitDirty,
        string? ArtifactPath,
        string? ArtifactSha256,
        string? TerraBaseArtifactPath,
        string? TerraBaseArtifactSha256);
}
