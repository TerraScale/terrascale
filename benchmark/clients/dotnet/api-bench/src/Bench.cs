using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace ApiBench;

/// <summary>One client run: a database, the SDK and raw transports, and the seeded corpora the steps use.</summary>
internal sealed class Bench : IDisposable
{
    private const int SeedParallelism = 16;
    private static readonly MediaTypeHeaderValue JsonType = new("application/json");

    private readonly Uri endpoint;
    private readonly string project;
    private readonly int seedDocuments;
    private readonly Tokens tokens;
    private readonly TerraScaleIdGenerator ids = new();
    private readonly CountingHandler sdkCounter;
    private readonly HttpClient sdk;
    private readonly TerraBaseDocumentsClient documents;
    private readonly RawTransport raw2;
    private readonly RawTransport raw1;
    private readonly HashSet<int> seeded = [];
    private readonly Dictionary<int, Corpus> corpora = [];
    private string database = "";
    private bool attempted;

    internal Bench(Uri endpoint, Uri issuer, string project, string apiKey, int seedDocuments)
    {
        this.endpoint = endpoint;
        this.project = project;
        this.seedDocuments = seedDocuments;
        var auth = new TerraScaleAuthClient(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }), new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri(issuer.GetLeftPart(UriPartial.Authority)),
            InstanceSlug = issuer.AbsolutePath.Trim('/'),
        });
        tokens = new Tokens(auth.Oidc, apiKey);
        sdkCounter = new CountingHandler(TerraBaseHttp.CreateHandler());
        sdk = new HttpClient(sdkCounter);
        documents = new TerraBaseDocumentsClient(sdk, new TerraBaseDocumentsClientOptions { BaseAddress = endpoint, AccessTokenProvider = tokens.GetAsync });
        raw2 = Raw(HttpVersion.Version20);
        raw1 = Raw(HttpVersion.Version11);
    }

    /// <summary>TerraBase database names match ^[a-z][a-z0-9_-]{0,62}$; document ids keep their uppercase ULIDs.</summary>
    internal static string DatabaseName(string id) => "bench-" + id.ToLowerInvariant();

    internal TerraBaseDatabase Db => documents.Database(project, database);

    /// <summary>Project-token create (<c>POST</c>) on every serving region; the legacy <c>PUT</c> route refuses project tokens.</summary>
    internal static Task<TerraBaseManagedDatabase> CreateDatabaseAsync(TerraBaseDocumentsClient documents, string project, string name, CancellationToken ct) =>
        documents.Project(project).CreateDatabaseAsync(name, allRegions: true, cancellationToken: ct);

    /// <summary>Token exchange, then database create. Nothing else is untimed setup.</summary>
    internal async Task<Ready> SetupAsync(CancellationToken ct)
    {
        var begin = Stopwatch.GetTimestamp();
        await tokens.GetAsync(ct);
        var authMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        database = DatabaseName(ids.NewId());
        attempted = true;
        await CreateDatabaseAsync(documents, project, database, ct);
        return new Ready("ready", Protocol.Client, Protocol.SdkName, Protocol.SdkVersion(), RuntimeInformation.FrameworkDescription, database,
            Stopwatch.GetElapsedTime(begin).TotalMilliseconds, authMs);
    }

    internal async Task<StepResult> StepAsync(Step step, CancellationToken interrupt)
    {
        var size = Payload.SizeFor(step.Scenario, step.PayloadBytes);
        try
        {
            await EnsureAsync(size, CancellationToken.None);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"seeding payload {size} failed: {error.GetType().Name} ({Http.StatusOf(error)})");
            return StepResult.Failed(step, DateTimeOffset.UtcNow);
        }

        return await new Driver(step, Operation(step), CounterOf(step.Transport)).RunAsync(interrupt);
    }

    /// <summary>Seeds size p the first time a step needs it. A failed attempt keeps its ids, so the retry re-PUTs the same documents.</summary>
    internal async Task EnsureAsync(int size, CancellationToken ct)
    {
        if (size == 0 || seeded.Contains(size)) return;
        if (!corpora.TryGetValue(size, out var corpus))
        {
            corpus = Corpus.Create(size, Enumerable.Range(0, seedDocuments).Select(_ => ids.NewId()).ToArray());
            corpora[size] = corpus;
        }

        var db = Db;
        foreach (var name in new[] { Name("seed", size), Name("upd", size), Name("ins", size), Name("txn", size) })
        {
            await CreateCollectionAsync(name, ct);
        }

        await Parallel.ForEachAsync(Enumerable.Range(0, seedDocuments), new ParallelOptions { MaxDegreeOfParallelism = SeedParallelism, CancellationToken = ct }, async (i, token) =>
        {
            await db.PutDocumentAsync(Name("seed", size), corpus.Ids[i], corpus.Docs[i % Payload.Groups], null, token);
            await db.PutDocumentAsync(Name("upd", size), corpus.Ids[i], corpus.Docs[i % Payload.Groups], null, token);
        });
        seeded.Add(size);
    }

    /// <summary>Builds the operation for one step. The corpus must already be seeded.</summary>
    internal Op Operation(Step step)
    {
        var size = Payload.SizeFor(step.Scenario, step.PayloadBytes);
        var corpus = corpora.GetValueOrDefault(size) ?? Corpus.None;
        var db = Db;
        var raw = (step.Transport == "raw-h2" ? raw2 : raw1).Client;
        var seed = Name("seed", size);
        var upd = Name("upd", size);
        var ins = Name("ins", size);
        var txn = Name("txn", size);
        long issued = 0;
        switch (step.Scenario, step.Transport)
        {
            case ("auth.token", _):
                return async (ct, _) =>
                {
                    await tokens.ExchangeAsync(ct);
                    return "ok";
                };
            case ("doc.get", "sdk"):
                return async (ct, _) => Http.ReadStatus(await db.GetDocumentAsync(seed, corpus.Id(), ct));
            case ("doc.get", _):
                return (ct, _) => SendAsync(raw, HttpMethod.Get, Http.ReadUri(endpoint, project, database, seed, corpus.Id()), null, ct);
            case ("doc.insert", "sdk"):
                return async (ct, _) =>
                {
                    await db.CreateDocumentAsync(ins, corpus.Docs[Payload.RandomGroup()], ct);
                    return "ok";
                };
            case ("doc.insert", _):
                return (ct, _) =>
                {
                    var body = new ByteArrayContent(corpus.Bodies[Payload.RandomGroup()]) { Headers = { ContentType = JsonType } };
                    return SendAsync(raw, HttpMethod.Post, Http.DocumentsUri(endpoint, project, database, ins), body, ct);
                };
            case ("doc.update", "sdk"):
                return async (ct, _) =>
                {
                    await db.PutDocumentAsync(upd, corpus.Id(), corpus.Docs[Payload.RandomGroup()], null, ct);
                    return "ok";
                };
            case ("doc.delete", "sdk"):
                return async (ct, timer) =>
                {
                    var created = await db.CreateDocumentAsync(ins, corpus.Docs[Payload.RandomGroup()], ct);
                    timer.Start = Stopwatch.GetTimestamp();
                    await db.DeleteDocumentAsync(ins, created.Id, null, ct);
                    return "ok";
                };
            case ("query.small", "sdk"):
                return async (ct, _) =>
                {
                    await db.QueryAsync(seed, new TerraBaseQuery { Filter = Payload.Filters[Payload.RandomGroup()], Limit = 10 }, ct);
                    return "ok";
                };
            case ("txn.batch", "sdk"):
                return async (ct, _) =>
                {
                    await db.TransactAsync(
                        [
                            TerraBaseTransactionOperation.Create(txn, corpus.Docs[Payload.RandomGroup()]),
                            TerraBaseTransactionOperation.Create(txn, corpus.Docs[Payload.RandomGroup()]),
                        ], ct);
                    return "ok";
                };
            case ("mixed.80-20", "sdk"):
                return async (ct, _) =>
                {
                    if (Interlocked.Increment(ref issued) % 5 == 0)
                    {
                        await db.PutDocumentAsync(seed, corpus.Id(), corpus.Docs[Payload.RandomGroup()], null, ct);
                        return "ok";
                    }

                    return Http.ReadStatus(await db.GetDocumentAsync(seed, corpus.Id(), ct));
                };
            default:
                throw new ArgumentException("unsupported scenario and transport combination");
        }
    }

    /// <summary>Deletes the database (logical deletion; the SDK documents remote erasure as durable, not synchronous). Only called after setup was attempted.</summary>
    internal async Task<Done> CleanupAsync()
    {
        if (!attempted) return new Done("done", true, "");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await Db.DeleteAsync(cts.Token);
            return new Done("done", true, "");
        }
        catch (TerraBaseException error)
        {
            return new Done("done", false, $"HTTP {(int?)error.StatusCode}");
        }
        catch (Exception error)
        {
            return new Done("done", false, error.GetType().Name);
        }
    }

    public void Dispose()
    {
        sdk.Dispose();
        raw2.Client.Dispose();
        raw1.Client.Dispose();
    }

    private CountingHandler CounterOf(string transport) => transport switch
    {
        "raw-h2" => raw2.Counter,
        "raw-h1" => raw1.Counter,
        _ => sdkCounter,
    };

    private async Task CreateCollectionAsync(string name, CancellationToken ct)
    {
        try
        {
            await Db.CreateCollectionAsync(name, ct);
        }
        catch (TerraBaseConflictException error) when (error.ErrorCode == "name_taken")
        {
            // Left by an earlier failed attempt at this size.
        }
    }

    /// <summary>One raw request with the cached JWT. The body is read in full so the counter sees it.</summary>
    private async Task<string> SendAsync(HttpClient http, HttpMethod method, Uri uri, HttpContent? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = body,
            Version = http.DefaultRequestVersion,
            VersionPolicy = http.DefaultVersionPolicy,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(ct));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await response.Content.CopyToAsync(Stream.Null, ct);
        return Http.CodeOf(response);
    }

    private static RawTransport Raw(Version version)
    {
        var counter = new CountingHandler(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = int.MaxValue,
        });
        var http = new HttpClient(counter)
        {
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new RawTransport(http, counter);
    }

    private static string Name(string kind, int size) => $"{kind}_{size}";

    private sealed record RawTransport(HttpClient Client, CountingHandler Counter);

    private sealed record Corpus(string[] Ids, JsonElement[] Docs, byte[][] Bodies)
    {
        internal static readonly Corpus None = new([], [], []);

        internal static Corpus Create(int size, string[] ids)
        {
            var texts = Enumerable.Range(0, Payload.Groups).Select(group => Payload.Json(group, size)).ToArray();
            return new(ids, texts.Select(Payload.Parse).ToArray(), texts.Select(text => Encoding.UTF8.GetBytes(text)).ToArray());
        }

        internal string Id() => Ids[Random.Shared.Next(Ids.Length)];
    }
}
