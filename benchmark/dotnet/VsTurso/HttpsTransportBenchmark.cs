using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraBase.E2E
{
    /// <summary>Public JWKS transport control; this does not measure authenticated document or engine work.</summary>
    [Category("benchmark-host")]
    public sealed class HttpsTransportBenchmark
    {
        [Test]
        [Timeout(600_000)]
        public async Task Compares_the_SDK_with_raw_HTTPS(CancellationToken cancellationToken)
        {
            var output = Environment.GetEnvironmentVariable("TERRABASE_HTTPS_TRANSPORT_OUT");
            Skip.When(string.IsNullOrEmpty(output), "run this public TLS control explicitly");
            var origin = new Uri(Environment.GetEnvironmentVariable("TERRABASE_HTTPS_TRANSPORT_ORIGIN") ?? "https://auth.terrascale.tech/");
            var label = Environment.GetEnvironmentVariable("TERRABASE_HTTPS_TRANSPORT_LABEL") ?? "unlabelled";
            if (origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0)
            {
                throw new ArgumentException("The transport control requires an HTTPS origin.");
            }

            await using var log = new StreamWriter(output!, append: false);
            await log.WriteLineAsync($"# host={Environment.MachineName} runtime={Environment.Version} serverGC={GCSettings.IsServerGC} SDK={label} origin={origin.Host}");
            await log.WriteLineAsync("| Repeat | c | Lane | Protocol | Connections | Cold wave ms | p50 ms | p99 ms | ops/s |");
            await log.WriteLineAsync("| ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |");
            for (var repeat = 1; repeat <= 3; repeat++)
            {
                foreach (var concurrency in new[] { 1, 16 })
                {
                    string[] lanes = repeat % 2 == 0 ? ["sdk", "raw-h2", "raw-h1"] : ["raw-h1", "raw-h2", "sdk"];
                    foreach (var lane in lanes)
                    {
                        var connections = 0;
                        using var sockets = TerraBaseHttp.CreateHandler();
                        sockets.ConnectCallback = async (context, token) =>
                        {
                            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                            try
                            {
                                await socket.ConnectAsync(context.DnsEndPoint, token);
                                Interlocked.Increment(ref connections);
                                return new NetworkStream(socket, ownsSocket: true);
                            }
                            catch
                            {
                                socket.Dispose();
                                throw;
                            }
                        };
                        using var observed = new ObservedTransport(sockets);
                        using var http = new HttpClient(observed) { Timeout = TimeSpan.FromSeconds(30), BaseAddress = origin };
                        var sdk = new TerraScaleAuthClient(http, new() { BaseAddress = origin, InstanceSlug = "terrascale" });
                        async Task GetAsync()
                        {
                            TerraScaleAuthJwkSet? keys;
                            if (lane == "sdk")
                            {
                                keys = await sdk.Oidc.GetJwksAsync(cancellationToken);
                            }
                            else
                            {
                                using var request = new HttpRequestMessage(HttpMethod.Get, "/terrascale/.well-known/jwks.json")
                                {
                                    Version = lane == "raw-h2" ? HttpVersion.Version20 : HttpVersion.Version11,
                                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                                };
                                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                                response.EnsureSuccessStatusCode();
                                await response.Content.LoadIntoBufferAsync(1_048_576, cancellationToken);
                                keys = JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(cancellationToken), HttpsTransportJson.Default.TerraScaleAuthJwkSet);
                            }

                            if (keys?.Keys.Count is not > 0)
                            {
                                throw new InvalidOperationException("The transport control did not return public signing keys.");
                            }
                        }

                        var cold = Stopwatch.GetTimestamp();
                        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => GetAsync()));
                        var coldMs = Stopwatch.GetElapsedTime(cold).TotalMilliseconds;
                        var samples = new double[concurrency * 64];
                        var began = Stopwatch.GetTimestamp();
                        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async worker =>
                        {
                            for (var index = 0; index < 64; index++)
                            {
                                var started = Stopwatch.GetTimestamp();
                                await GetAsync();
                                samples[worker * 64 + index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            }
                        }));
                        var elapsed = Stopwatch.GetElapsedTime(began).TotalSeconds;
                        Array.Sort(samples);
                        var protocols = string.Join(',', observed.Protocols.Keys.Order(StringComparer.Ordinal));
                        await log.WriteLineAsync(FormattableString.Invariant($"| {repeat} | {concurrency} | {lane} | {protocols} | {connections} | {coldMs:F2} | {samples[samples.Length / 2]:F2} | {samples[(int)((samples.Length - 1) * 0.99)]:F2} | {samples.Length / elapsed:F1} |"));
                        await log.FlushAsync(cancellationToken);
                    }
                }
            }
        }

        private sealed class ObservedTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            public ConcurrentDictionary<string, byte> Protocols { get; } = new(StringComparer.Ordinal);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                Protocols.TryAdd(response.Version.ToString(), 0);
                return response;
            }
        }
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(TerraScaleAuthJwkSet))]
    internal sealed partial class HttpsTransportJson : JsonSerializerContext;
}
