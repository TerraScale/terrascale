using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Serialization;
using MagicOnion.Serialization.MessagePack;
using MessagePack;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Platform.Rpc
{
    // MagicOnion clients for TerraBase's internal listener (h2c on the node's NetBird address). Every call carries the
    // shared peer credential. User clients add the signed-in user's scoped ts-auth token, so TerraBase authorizes,
    // places and meters the call exactly as on its public API; platform clients act as the operator on the dashboard's
    // own state. The local region's listener comes from TS_DASHBOARD_TERRABASE_URL; another region's from TS_PEERS_FILE.
    public sealed class TerraBaseRpcClient : IDisposable
    {
        public const string OriginName = "TS_DASHBOARD_TERRABASE_URL";
        public const string MeterName = "TerraScale.Dashboard.TerraBaseRpc";

        private static readonly IMagicOnionSerializerProvider Serializer = MessagePackMagicOnionSerializerProvider.Default.WithOptions(TerraBaseRpc.SerializerOptions);
        private readonly ConcurrentDictionary<Uri, GrpcChannel> channels = new();
        private readonly IDashboardIdentity identity;
        private readonly Uri local;
        private readonly string? region;
        private readonly string? peersFile;
        private readonly Counter<long> calls;
        private readonly Histogram<double> durations;

        public TerraBaseRpcClient(IDashboardIdentity identity, IHostEnvironment environment, IMeterFactory meters)
            : this(identity, Origin(environment), Environment.GetEnvironmentVariable("TS_REGION"), Environment.GetEnvironmentVariable("TS_PEERS_FILE"), meters)
        {
        }

        public TerraBaseRpcClient(IDashboardIdentity identity, Uri local, string? region, string? peersFile, IMeterFactory meters)
        {
            ArgumentNullException.ThrowIfNull(meters);
            this.identity = identity;
            this.local = local;
            this.region = region is { Length: > 0 } ? region : null;
            this.peersFile = peersFile is { Length: > 0 } ? peersFile : null;
            var meter = meters.Create(MeterName);
            calls = meter.CreateCounter<long>("dashboard.terrabase.rpc.calls", description: "TerraBase MagicOnion calls by service, method and outcome.");
            durations = meter.CreateHistogram<double>("dashboard.terrabase.rpc.duration", "s", "TerraBase MagicOnion call durations.");
        }

        // The listener's origin; only Development defaults to the loopback TerraBase.
        public static Uri Origin(IHostEnvironment environment)
        {
            ArgumentNullException.ThrowIfNull(environment);
            return Origin(environment, Environment.GetEnvironmentVariable(OriginName));
        }

        public static Uri Origin(IHostEnvironment environment, string? configured)
        {
            ArgumentNullException.ThrowIfNull(environment);
            return configured is { Length: > 0 } ? Configured(configured)
            : environment.IsDevelopment() ? new("http://127.0.0.1:8081/")
            : throw new InvalidOperationException($"Set {OriginName}: only Development defaults to the loopback TerraBase.");
        }

        // The operator's client, for the dashboard's own state in the local region.
        public T Platform<T>() where T : IService<T>
        {
            return Create<T>(local);
        }

        // A client that acts with the user's scoped ts-auth token in the region that serves the database (local when null).
        public T User<T>(string accessToken, string? databaseRegion = null) where T : IService<T>
        {
            ArgumentException.ThrowIfNullOrEmpty(accessToken);
            return Create<T>(Endpoint(databaseRegion)).WithHeaders(new Metadata { { TerraBaseRpc.AuthorizationHeader, "Bearer " + accessToken } });
        }

        public void Dispose()
        {
            foreach (var channel in channels.Values)
            {
                channel.Dispose();
            }
        }

        private T Create<T>(Uri endpoint) where T : IService<T>
        {
            var channel = channels.GetOrAdd(endpoint, Open);
            return MagicOnionClient.Create<T>(channel.Intercept(new Faults(this)), Serializer);
        }

        private GrpcChannel Open(Uri endpoint)
        {
            var credential = CallCredentials.FromInterceptor(async (context, metadata) =>
            {
                var credentials = await identity.ReadAsync(context.CancellationToken);
                metadata.Add(TerraBaseRpc.PeerCredentialHeader, credentials.TerraBasePeerSecret);
            });
            return GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false,
                    EnableMultipleHttp2Connections = true,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                },
                Credentials = ChannelCredentials.Create(ChannelCredentials.Insecure, credential),
                // The listener is h2c on the WireGuard-encrypted NetBird network (or loopback), as the state HTTP client was.
                UnsafeUseInsecureChannelCallCredentials = true,
                MaxReceiveMessageSize = TerraBaseRpc.MaxMessageBytes,
                MaxSendMessageSize = TerraBaseRpc.MaxMessageBytes,
                DisposeHttpClient = true,
            });
        }

        // The local listener, or a NetBird peer of the database's region on the same port.
        private Uri Endpoint(string? databaseRegion)
        {
            if (databaseRegion is null || databaseRegion == region || peersFile is null)
            {
                return local;
            }

            foreach (var line in File.ReadLines(peersFile))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields is [_, var peerRegion, var address] && peerRegion == databaseRegion && IPAddress.TryParse(address, out var ip))
                {
                    return new UriBuilder(local.Scheme, ip.ToString(), local.Port).Uri;
                }
            }

            throw new TerraBaseUnavailableException(new TerraBaseFault(503, "region_unavailable", $"No TerraBase peer serves region {databaseRegion}."));
        }

        private static Uri Configured(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" && uri.UserInfo.Length == 0 &&
            uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
                ? uri
                : throw new InvalidOperationException($"{OriginName} must be an http origin such as http://127.0.0.1:8081.");
        }

        // Turns TerraBase refusals into TerraBaseException and records every call's outcome.
        private sealed class Faults(TerraBaseRpcClient owner) : Interceptor
        {
            public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
                TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
            {
                var started = Stopwatch.GetTimestamp();
                var call = continuation(request, context);
                var response = ServerTiming.RpcAsync(context.Method.ServiceName + "/" + context.Method.Name, Mutates(context.Method.Name),
                    () => Await(call.ResponseAsync, context.Method, started));
                return new AsyncUnaryCall<TResponse>(response, call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
            }

            // Reads are named as such; any other call may change state, so it invalidates the request's earlier reads.
            private static bool Mutates(string method)
            {
                return !(method.StartsWith("Get", StringComparison.Ordinal) || method.StartsWith("List", StringComparison.Ordinal) ||
                    method.StartsWith("Query", StringComparison.Ordinal) || method.StartsWith("Read", StringComparison.Ordinal) ||
                    method.StartsWith("Health", StringComparison.Ordinal) || method.StartsWith("Preview", StringComparison.Ordinal) ||
                    method.StartsWith("Stream", StringComparison.Ordinal) || method.StartsWith("Export", StringComparison.Ordinal));
            }

            private async Task<TResponse> Await<TResponse>(Task<TResponse> response, IMethod method, long started)
            {
                var outcome = "ok";
                try
                {
                    return await response;
                }
                catch (RpcException error)
                {
                    var fault = error.Trailers.GetValueBytes(TerraBaseRpc.FaultTrailer) is { } bytes
                        ? MessagePackSerializer.Deserialize<TerraBaseFault>(bytes, TerraBaseRpc.SerializerOptions)
                        : Transport(error);
                    outcome = fault.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    throw TerraBaseException.From(fault, error);
                }
                finally
                {
                    var tags = new TagList { { "service", method.ServiceName }, { "method", method.Name }, { "outcome", outcome } };
                    owner.calls.Add(1, tags);
                    owner.durations.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
                }
            }

            // A refusal without a fault came from the transport or the listener, not from a TerraBase route.
            private static TerraBaseFault Transport(RpcException error)
            {
                return error.StatusCode switch
                {
                    StatusCode.Unauthenticated => new(401, "unauthenticated", "TerraBase refused the peer credential."),
                    StatusCode.DeadlineExceeded => new(504, "deadline_exceeded", "The TerraBase call timed out."),
                    StatusCode.Cancelled => throw new OperationCanceledException(error.Message, error),
                    _ => new(503, "unavailable", "TerraBase is unreachable: " + error.Status.Detail),
                };
            }
        }
    }
}
