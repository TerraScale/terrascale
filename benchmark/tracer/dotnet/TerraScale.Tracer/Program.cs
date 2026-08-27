// TerraScale.Tracer — the SE-11 .NET SDK tracer (ticket #13). One
// console process embedding the real TerraScale.DotNet
// `terrascale.sync.v2` client against the TerraBase C# API host, driven
// by a scripted plan, emitting the same uniform JSONL observation log
// as the TypeScript tracer (the orchestrator diffs the two and asserts
// both against the SE-04 golden vectors).
//
// No secrets, no live systems: a loopback host only, backend-context
// fixtures only. Payloads stay opaque; the tracer records payload byte
// identities (SHA-256 digests), never payload contents.

using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.DotNet;

namespace Terrabase.Tracer;

public static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            Console.Error.WriteLine("usage: TerraScale.Tracer <spec.json>");
            return 2;
        }

        var spec = JsonDocument.Parse(await File.ReadAllTextAsync(arguments[0]).ConfigureAwait(false)).RootElement;
        using var log = new ObservationLog(spec.GetProperty("out").GetString()!);
        var tracer = new SdkTracer(spec, log);
        return await tracer.RunAsync().ConfigureAwait(false);
    }
}

/// <summary>The tracer's one JSON serialization settings (camelCase wire names).</summary>
internal static class TracerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>The serialized JSONL observation log (one writer, many sources).</summary>
public sealed class ObservationLog : IDisposable
{
    private readonly StreamWriter writer;
    private readonly object gate = new();

    public ObservationLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        this.writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Emit(object record)
    {
        lock (this.gate)
        {
            this.writer.WriteLine(JsonSerializer.Serialize(record, TracerJson.Options));
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.writer.Dispose();
        }
    }
}

/// <summary>
/// An observing HTTP handler: records the client-side wire facts (method,
/// path, status) without touching a byte of any request or response. The
/// SE-11 plans gate a measured write on the one fact this handler can
/// prove — that the SDK live loop's push-channel subscription (the
/// <c>/v1/streams</c> GET) was accepted by the host: the host registers
/// the subscriber (and the engine's backbone subscription) before it
/// writes the stream's first event, so the subscription being observable
/// on the client means every later trigger fans out to it. The .NET live
/// loop has no fallback pull before its first trigger (finding 3 in the
/// packet record), so a commit landing between the session setup and that
/// subscription is otherwise undeliverable.
/// </summary>
internal sealed class ObservingHandler : DelegatingHandler
{
    private readonly Action<HttpRequestMessage, HttpResponseMessage> observe;

    public ObservingHandler(Action<HttpRequestMessage, HttpResponseMessage> observe)
        : base(new SocketsHttpHandler())
    {
        this.observe = observe;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        this.observe(request, response);
        return response;
    }
}

/// <summary>One scripted tracer run over the real SDK session.</summary>
public sealed class SdkTracer
{
    private readonly JsonElement spec;
    private readonly ObservationLog log;
    private readonly string gates;
    private readonly string checkpointFile;
    private CancellationTokenSource stopped = new();
    private HttpClient? http;
    private TerraScaleSyncV2Client? client;
    private TerraScaleSyncV2Session? session;
    private Task? liveLoop;
    private TaskCompletionSource<bool>? streamSubscribed;

    public SdkTracer(JsonElement spec, ObservationLog log)
    {
        this.spec = spec;
        this.log = log;
        this.gates = spec.GetProperty("gates").GetString()!;
        this.checkpointFile = spec.GetProperty("checkpointFile").GetString()!;
    }

    public async Task<int> RunAsync()
    {
        this.Log(new { t = "tracer", role = this.SpecString("role"), sdk = "TerraScale.DotNet", dotnet = Environment.Version.ToString() });
        foreach (var step in this.spec.GetProperty("plan").EnumerateArray())
        {
            try
            {
                await this.RunStepAsync(step).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                // A failed step is an observation, not a crash: the plan
                // owns the recovery choreography; the orchestrator asserts
                // on the log.
                var host = failure as TerraScaleSyncV2HostException;
                this.Log(new
                {
                    t = "error",
                    op = SdkTracer.SpecStringOf(step, "op"),
                    code = host?.Code ?? failure.GetType().Name,
                    statusCode = host is null ? (int?)null : (int)host.StatusCode,
                    resyncRequired = host?.ResyncRequired == true,
                    message = failure.Message,
                });
            }
        }

        return 0;
    }

    private async Task RunStepAsync(JsonElement step)
    {
        switch (step.GetProperty("op").GetString())
        {
            case "connect":
                await this.ConnectAsync(step).ConfigureAwait(false);
                break;
            case "mutate":
                await this.MutateAsync(step).ConfigureAwait(false);
                break;
            case "pull":
                _ = await this.Session().PullDeltaAsync(this.stopped.Token).ConfigureAwait(false);
                await this.AcknowledgeDurableAsync().ConfigureAwait(false);
                break;
            case "resync":
                _ = await this.Session().PullSnapshotAsync(this.stopped.Token).ConfigureAwait(false);
                await this.AcknowledgeDurableAsync().ConfigureAwait(false);
                break;
            case "waitCheckpoint":
                await this.WaitCheckpointAsync(step.GetProperty("sequence").GetUInt64(), IntOrDefault(step, "timeoutMs", 20_000)).ConfigureAwait(false);
                break;
            case "waitStream":
                await this.WaitStreamAsync(IntOrDefault(step, "timeoutMs", 20_000)).ConfigureAwait(false);
                break;
            case "gate":
                await this.WaitGateAsync(step.GetProperty("name").GetString()!, IntOrDefault(step, "timeoutMs", 60_000)).ConfigureAwait(false);
                break;
            case "signal":
                await File.WriteAllTextAsync(Path.Combine(this.gates, step.GetProperty("name").GetString()!), "1").ConfigureAwait(false);
                break;
            case "watch":
                await Task.Delay(step.GetProperty("ms").GetInt32(), this.stopped.Token).ConfigureAwait(false);
                break;
            case "dispose":
                await this.DisposeSessionAsync().ConfigureAwait(false);
                break;
            case "final":
                this.EmitFinal();
                break;
            default:
                throw new InvalidOperationException($"unknown plan op {step.GetProperty("op")}");
        }
    }

    private async Task ConnectAsync(JsonElement step)
    {
        await this.DisposeSessionAsync().ConfigureAwait(false);
        var url = (step.TryGetProperty("host", out var hostElement) ? hostElement.GetString() : null) ?? this.SpecString("host");

        TerraScaleSyncV2SessionOptions options = new(this.SpecString("collection"))
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(IntOrDefault(this.spec, "reconnectDelayMs", 250)),
            MaximumReconnectDelay = TimeSpan.FromMilliseconds(IntOrDefault(this.spec, "reconnectMaxMs", 5_000)),
        };
        string? resume = null;
        if (step.TryGetProperty("resume", out var resumeElement) && resumeElement.GetBoolean())
        {
            resume = File.Exists(this.checkpointFile) ? await File.ReadAllTextAsync(this.checkpointFile).ConfigureAwait(false) : "";
        }

        if (!string.IsNullOrEmpty(resume))
        {
            options = options with { Resume = TerraScaleSyncV2Checkpoint.FromBytes(Convert.FromBase64String(resume)) };
        }

        // The subscription observation of THIS connection: the gate the
        // plan's waitStream step (and through it the orchestrator's
        // measured writes) waits on. Per connection, because the plan may
        // dispose and re-connect (a drop, a route change, a fresh setup).
        var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.streamSubscribed = subscribed;
        var streamsPath = options.StreamsPath;
        this.http = new HttpClient(
            new ObservingHandler((request, response) =>
            {
                if (!response.IsSuccessStatusCode
                    || request.Method != HttpMethod.Get
                    || request.RequestUri?.AbsolutePath != streamsPath)
                {
                    return;
                }

                this.Log(new { t = "stream-opened", path = streamsPath, status = (int)response.StatusCode });
                subscribed.TrySetResult(true);
            }))
        {
            BaseAddress = new Uri(url),
        };
        this.client = new TerraScaleSyncV2Client(this.http, new TerraScaleSyncV2ClientOptions
        {
            Principal = this.SpecString("principal"),
            Tenant = this.SpecString("tenant"),
        });
        this.session = this.client.CreateSession(options);
        var session = this.session;
        // The live loop runs the trigger-driven path: setup (resuming or
        // fresh), the initial snapshot adoption (fresh sessions), and the
        // pull-per-trigger delivery — exactly the SDK's live surface.
        this.liveLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in session.UpdatesAsync(this.stopped.Token).ConfigureAwait(false))
                {
                    this.SaveDurable(session.State.Checkpoint);
                    this.Log(new
                    {
                        t = update.Kind switch
                        {
                            TerraScaleSyncV2SyncUpdateKind.SnapshotAdopted => "snapshot-adopted",
                            TerraScaleSyncV2SyncUpdateKind.ResyncAdopted => "snapshot-adopted",
                            TerraScaleSyncV2SyncUpdateKind.DuplicateDelta => "duplicate-delta",
                            _ => "delta-applied",
                        },
                        checkpoint = CheckpointOf(update.Checkpoint),
                        codec = CodecOf(update.Codec),
                        commits = update.Commits,
                        payloadSha256 = Sha256(update.Payload),
                        payloadBytes = update.Payload.Length,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal: the loop ends by design.
            }
            catch (Exception failure)
            {
                var host = failure as TerraScaleSyncV2HostException;
                this.Log(new
                {
                    t = "error",
                    op = "live-loop",
                    code = host?.Code ?? failure.GetType().Name,
                    statusCode = host is null ? (int?)null : (int)host.StatusCode,
                    resyncRequired = host?.ResyncRequired == true,
                    message = failure.Message,
                });
            }
        });

        // The setup completes inside the live loop; observe its answer.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (session.SessionId == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }

        this.Log(new
        {
            t = "opened",
            sessionId = session.SessionId,
            serverCheckpoint = session.State.ServerCheckpoint is { } server ? CheckpointOf(server) : null,
            resumed = !string.IsNullOrEmpty(resume),
        });
    }

    private async Task MutateAsync(JsonElement step)
    {
        var document = step.GetProperty("document").GetString()!;
        var key = step.GetProperty("key").GetString()!;
        TerraScaleSyncV2DocumentWrite write = step.TryGetProperty("delete", out var delete) && delete.GetBoolean()
            ? new TerraScaleSyncV2DocumentWrite.Delete()
            : new TerraScaleSyncV2DocumentWrite.Put(FieldsOf(step.GetProperty("put")));
        var outcome = await this.Session().MutateAsync(document, write, key, this.stopped.Token).ConfigureAwait(false);
        if (outcome is TerraScaleSyncV2MutationApplied applied)
        {
            this.Log(new { t = "mutation", key, document, outcome = "applied", sequence = applied.Sequence, operationId = applied.OperationId });
        }
        else if (outcome is TerraScaleSyncV2MutationRejected rejected)
        {
            this.Log(new { t = "mutation", key, document, outcome = "rejected", reason = rejected.WireName });
        }
    }

    private async Task WaitCheckpointAsync(ulong sequence, int timeoutMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (this.session?.State.Checkpoint.StorageSequence >= sequence)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException($"checkpoint timeout waiting for storage sequence {sequence}");
    }

    /// <summary>
    /// Waits for positive evidence that this session's push channel is
    /// subscribed: the streams response observed on the client's own wire.
    /// The deterministic gate a measured write is held behind (the .NET
    /// live loop has no fallback pull before its first trigger, so a
    /// commit landing before the subscription is undeliverable — finding 3
    /// of the packet record).
    /// </summary>
    private async Task WaitStreamAsync(int timeoutMs)
    {
        var subscribed = this.streamSubscribed ?? throw new InvalidOperationException("the session is not open");
        try
        {
            await subscribed.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"the delivery stream was not subscribed within {timeoutMs} ms");
        }
    }

    private async Task WaitGateAsync(string name, int timeoutMs)
    {
        var path = Path.Combine(this.gates, name);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        if (!File.Exists(path))
        {
            throw new TimeoutException($"gate timeout waiting for {name}");
        }
    }

    private async Task AcknowledgeDurableAsync()
    {
        var session = this.Session();
        await session.AcknowledgeCheckpointAsync(this.stopped.Token).ConfigureAwait(false);
        this.SaveDurable(session.State.Checkpoint);
    }

    private async Task DisposeSessionAsync()
    {
        if (this.session is not null)
        {
            this.stopped.Cancel();
            this.session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            if (this.liveLoop is not null)
            {
                try
                {
                    await this.liveLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The loop is winding down; its records are already in the log.
                }
            }

            this.session = null;
            this.liveLoop = null;
            this.streamSubscribed = null;
            // A fresh cancellation source per session: a reset of the
            // cancelled one is not guaranteed once callbacks registered,
            // and the next session must not inherit a cancelled token.
            this.stopped = new CancellationTokenSource();
            this.http?.Dispose();
            this.http = null;
            this.Log(new { t = "closed" });
        }
    }

    private void EmitFinal()
    {
        var state = this.session?.State;
        this.Log(new
        {
            t = "final",
            status = this.session is null ? "disposed" : "live",
            checkpoint = state is null ? null : CheckpointOf(state.Checkpoint),
            appliedDeltaBatches = state?.DeltasApplied,
            duplicateDeltas = state?.DuplicateDeltas,
            adoptedSnapshots = state?.ResyncsAdopted,
            triggersObserved = state?.TriggersObserved,
            mutationsAcknowledged = state?.MutationsAcknowledged,
        });
    }

    private TerraScaleSyncV2Session Session() =>
        this.session ?? throw new InvalidOperationException("the session is not open");

    private void SaveDurable(TerraScaleSyncV2Checkpoint checkpoint) =>
        File.WriteAllText(this.checkpointFile, Convert.ToBase64String(checkpoint.ToBytes()));

    private string SpecString(string name) => this.spec.GetProperty(name).GetString()!;

    private static string? SpecStringOf(JsonElement step, string name) =>
        step.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static int IntOrDefault(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : fallback;

    private static Dictionary<string, TerraScaleSyncV2Value> FieldsOf(JsonElement put)
    {
        var fields = new Dictionary<string, TerraScaleSyncV2Value>();
        foreach (var field in put.EnumerateObject())
        {
            fields[field.Name] = field.Value.ValueKind switch
            {
                JsonValueKind.String => TerraScaleSyncV2Value.Of(field.Value.GetString()!),
                JsonValueKind.Number => field.Value.TryGetInt64(out var integer)
                    ? TerraScaleSyncV2Value.Of(integer)
                    : TerraScaleSyncV2Value.Of(field.Value.GetDouble()),
                JsonValueKind.True => TerraScaleSyncV2Value.Of(true),
                JsonValueKind.False => TerraScaleSyncV2Value.Of(false),
                JsonValueKind.Null => TerraScaleSyncV2Value.Null,
                _ => throw new InvalidOperationException($"the plan value of {field.Name} is not scalar"),
            };
        }

        return fields;
    }

    private static object CheckpointOf(TerraScaleSyncV2Checkpoint checkpoint) => new
    {
        storageSequence = checkpoint.StorageSequence,
        ends = checkpoint.Ends.Select(end => new object[] { end.Peer, end.End }).ToArray(),
    };

    private static string? CodecOf(TerraScaleSyncV2PayloadCodec? codec) => codec switch
    {
        TerraScaleSyncV2PayloadCodec.LoroDelta1 => "loro-delta/1",
        TerraScaleSyncV2PayloadCodec.LoroSnapshot1 => "loro-snapshot/1",
        _ => null,
    };

    private static string Sha256(ReadOnlyMemory<byte> payload)
    {
        var digest = SHA256.HashData(payload.Span);
        return Convert.ToHexStringLower(digest);
    }

    private void Log(object record) => this.log.Emit(record);
}
