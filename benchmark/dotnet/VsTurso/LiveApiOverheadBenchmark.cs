using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraBase.E2E
{
    /// <summary>Authenticated SDK/raw-JSON reads of this run's synthetic database in every discovered serving region.</summary>
    [Category("benchmark-host")]
    public sealed class LiveApiOverheadBenchmark
    {
        [Test]
        [Timeout(30 * 60 * 1000)]
        public async Task Measures_authenticated_regional_round_trips(CancellationToken cancellationToken)
        {
            var input = Environment.GetEnvironmentVariable("TERRABASE_API_OVERHEAD_CREDENTIALS");
            Skip.When(string.IsNullOrEmpty(input), "run through benchmark/scripts/api-overhead-tenant.py");
            var output = Environment.GetEnvironmentVariable("TERRABASE_API_OVERHEAD_OUT") ?? throw new InvalidOperationException("An absolute output path is required.");
            if (!Path.IsPathFullyQualified(output))
            {
                throw new InvalidOperationException("An absolute output path is required.");
            }

            var credentials = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(input!, cancellationToken), LiveApiJson.Default.LiveCredentials)
                ?? throw new InvalidOperationException("Benchmark credentials are missing.");
            using var authHttp = new HttpClient(TerraBaseHttp.CreateHandler());
            var auth = new TerraScaleAuthClient(authHttp, new() { BaseAddress = new("https://auth.terrascale.tech/"), InstanceSlug = "terrascale" });
            using var tokens = new LiveTokens(auth.Oidc, credentials);
            using var discovery = new HttpClient(TerraBaseHttp.CreateHandler());
            var regions = (await new TerraBaseRegionsClient(discovery).ListRegionsAsync(cancellationToken)).Regions;
            if (new[] { "gru", "sjc", "fra" }.Except(regions, StringComparer.Ordinal).Any())
            {
                throw new InvalidOperationException("The required serving regions are absent from discovery.");
            }

            var home = regions.Contains("sjc") ? "sjc" : regions[0];
            using var setupHttp = new HttpClient(TerraBaseHttp.CreateHandler());
            var setup = new TerraBaseDocumentsClient(setupHttp, new() { Region = home, AccessTokenProvider = tokens.GetAsync });
            var project = setup.Project(credentials.Project);
            var name = "api-perf-" + EntityIds.New().ToString().ToLowerInvariant();
            var created = false;
            await using var log = new StreamWriter(output);
            await log.WriteLineAsync($"# host={Environment.MachineName} runtime={Environment.Version} regions={string.Join(',', regions)}");
            await log.WriteLineAsync("| Region | Repeat | Bytes | c | Lane | Protocol | p50 ms | p99 ms | ops/s |");
            await log.WriteLineAsync("| --- | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: |");
            try
            {
                _ = await project.CreateDatabaseAsync(name, homeRegion: home, allRegions: true, cancellationToken: cancellationToken);
                created = true;
                var database = setup.Database(credentials.Project, name);
                _ = await database.CreateCollectionAsync("bench", cancellationToken);
                var documents = new List<(int Bytes, string Id, string Version)>();
                foreach (var size in new[] { 256, 4096 })
                {
                    var id = EntityIds.New().ToString();
                    using var json = JsonDocument.Parse("{\"payload\":\"" + new string('x', size - 14) + "\"}");
                    var written = await database.PutDocumentAsync("bench", id, json.RootElement, cancellationToken: cancellationToken);
                    documents.Add((size, id, written.Version));
                }

                for (var repeat = 1; repeat <= 3; repeat++)
                {
                    foreach (var region in regions)
                    {
                        using var observed = new LiveTransport(TerraBaseHttp.CreateHandler(), region);
                        using var http = new HttpClient(observed) { BaseAddress = TerraBaseRegion.PublicOrigin, Timeout = TimeSpan.FromSeconds(30) };
                        var sdk = new TerraBaseDocumentsClient(http, new() { Region = region, AccessTokenProvider = tokens.GetAsync })
                            .Database(credentials.Project, name);
                        foreach (var document in documents)
                        {
                            // A bounded, condition-driven wait for this run's newly created replica; never timed as request overhead.
                            using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            ready.CancelAfter(TimeSpan.FromSeconds(60));
                            while (await sdk.GetDocumentAsync("bench", document.Id, ready.Token) is null)
                            {
                                await Task.Delay(100, ready.Token);
                            }

                            foreach (var concurrency in new[] { 1, 16 })
                            {
                                string[] lanes = repeat % 2 == 0 ? ["sdk", "raw-h2", "raw-h1"] : ["raw-h1", "raw-h2", "sdk"];
                                foreach (var lane in lanes)
                                {
                                    observed.Protocols.Clear();
                                    async Task ReadAsync()
                                    {
                                        if (lane == "sdk")
                                        {
                                            var result = await sdk.GetDocumentAsync("bench", document.Id, cancellationToken);
                                            if (result?.Id != document.Id || result.Version != document.Version)
                                            {
                                                throw new InvalidOperationException("SDK document identity/version mismatch.");
                                            }
                                        }
                                        else
                                        {
                                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                            deadline.CancelAfter(TimeSpan.FromSeconds(30));
                                            using var request = new HttpRequestMessage(HttpMethod.Get,
                                                $"/v1/projects/{credentials.Project}/databases/{name}/collections/bench/documents/by-key?id={document.Id}")
                                            {
                                                Version = lane == "raw-h2" ? HttpVersion.Version20 : HttpVersion.Version11,
                                                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                                            };
                                            request.Headers.Add(TerraBaseRegion.PinHeader, region);
                                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(deadline.Token));
                                            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                                            response.EnsureSuccessStatusCode();
                                            await response.Content.LoadIntoBufferAsync(1_048_576, deadline.Token);
                                            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
                                            if (body.RootElement.GetProperty("id").GetString() != document.Id
                                                || body.RootElement.GetProperty("version").GetString() != document.Version)
                                            {
                                                throw new InvalidOperationException("Raw document identity/version mismatch.");
                                            }
                                        }
                                    }

                                    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => ReadAsync()));
                                    var samples = new double[concurrency * 64];
                                    var started = Stopwatch.GetTimestamp();
                                    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async worker =>
                                    {
                                        for (var index = 0; index < 64; index++)
                                        {
                                            var began = Stopwatch.GetTimestamp();
                                            await ReadAsync();
                                            samples[worker * 64 + index] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                                        }
                                    }));
                                    var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                                    Array.Sort(samples);
                                    await log.WriteLineAsync(FormattableString.Invariant($"| {region} | {repeat} | {document.Bytes} | {concurrency} | {lane} | {string.Join(',', observed.Protocols.Keys.Order(StringComparer.Ordinal))} | {samples[samples.Length / 2]:F2} | {samples[(int)((samples.Length - 1) * 0.99)]:F2} | {samples.Length / elapsed:F1} |"));
                                    await log.FlushAsync(cancellationToken);
                                }
                            }
                        }
                    }
                }

                await log.WriteLineAsync($"# Client-credentials exchanges: {tokens.Exchanges}; all cells returned this run's document versions.");
            }
            catch
            {
                throw new InvalidOperationException("Live API measurement failed; credentials and response content are redacted.");
            }
            finally
            {
                if (created)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await project.DeleteDatabaseAsync(name, cleanup.Token);
                    await log.WriteLineAsync("# Synthetic database logical deletion accepted; account helper verifies closure settlement.");
                }
            }
        }

        private sealed class LiveTransport(HttpMessageHandler inner, string region) : DelegatingHandler(inner)
        {
            public ConcurrentDictionary<string, byte> Protocols { get; } = new(StringComparer.Ordinal);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (!response.Headers.TryGetValues("X-TerraBase-Region", out var served) || served.Single() != region)
                {
                    response.Dispose();
                    throw new InvalidOperationException("Regional response identity did not match the pin.");
                }

                Protocols.TryAdd(response.Version.ToString(), 0);
                return response;
            }
        }
    }

    internal sealed record LiveCredentials([property: JsonPropertyName("api_key")] string ApiKey, [property: JsonPropertyName("project")] string Project);

    internal sealed class LiveTokens(TerraScaleAuthOidcClient oidc, LiveCredentials credentials) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CachedToken? _cached;
        private int _exchanges;

        public int Exchanges => Volatile.Read(ref _exchanges);

        public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _cached) is { } current && Stopwatch.GetTimestamp() < current.RenewAt)
            {
                return current.Token;
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_cached is null || Stopwatch.GetTimestamp() >= _cached.RenewAt)
                {
                    var sent = Stopwatch.GetTimestamp();
                    var grant = await oidc.RequestClientCredentialsTokenAsync(credentials.ApiKey[..26], credentials.ApiKey, project: credentials.Project, cancellationToken: cancellationToken);
                    Interlocked.Increment(ref _exchanges);
                    Volatile.Write(ref _cached, new CachedToken(grant.AccessToken, sent + (long)(grant.ExpiresIn * 0.9 * Stopwatch.Frequency)));
                }

                return _cached.Token;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _gate.Dispose();
        }

        private sealed record CachedToken(string Token, long RenewAt);
    }

    [JsonSerializable(typeof(LiveCredentials))]
    internal sealed partial class LiveApiJson : JsonSerializerContext;
}
