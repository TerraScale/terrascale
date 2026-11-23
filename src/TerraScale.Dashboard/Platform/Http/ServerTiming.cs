using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Routing;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Platform.Http
{
    public sealed class ServerTiming
    {
        private static readonly AsyncLocal<ServerTiming?> Current = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Lock gate = new();
        private readonly Dictionary<string, (int Calls, double Ms)> upstreams = new(StringComparer.Ordinal);
        private readonly List<(string Upstream, double Start, double End)> spans = [];
        private ILogger? logger;
        private readonly Dictionary<object, (int Writes, object Task)> readTasks = [];
        private int writes;
        private int pendingWrites;
        private bool reads;
        private bool probe;

        public sealed record Mark(ServerTiming Request, int Writes)
        {
            public bool Current => ServerTiming.Current.Value == Request && Volatile.Read(ref Request.writes) == Writes && Volatile.Read(ref Request.pendingWrites) == 0;
        }

        public static Mark? Now()
        {
            return Current.Value is { pendingWrites: 0 } timing ? new(timing, Volatile.Read(ref timing.writes)) : null;
        }

        public static Task<T> ReadAsync<T>(object key, Func<Task<T>> read)
        {
            return ReadAsync(key, read, false);
        }

        public static Task<T> PublicReadAsync<T>(object key, Func<Task<T>> read)
        {
            return ReadAsync(key, read, true);
        }

        private static Task<T> ReadAsync<T>(object key, Func<Task<T>> read, bool publicMetadata)
        {
            if (Current.Value is not { reads: true } timing)
            {
                return read();
            }

            (Task<Task<T>> Cold, Task<T> Shared)? registered = null;
            lock (timing.gate)
            {
                if (publicMetadata || Volatile.Read(ref timing.pendingWrites) == 0)
                {
                    var generation = publicMetadata ? -1 : Volatile.Read(ref timing.writes);
                    if (timing.readTasks.TryGetValue(key, out var entry) && entry.Writes == generation &&
                        entry.Task is Task<T> pending && !pending.IsFaulted && !pending.IsCanceled)
                    {
                        return pending;
                    }

                    var cold = new Task<Task<T>>(read);
                    var shared = cold.Unwrap();
                    timing.readTasks[key] = (generation, shared);
                    registered = (cold, shared);
                }
            }

            if (registered is not { } started)
            {
                return read();
            }

            started.Cold.RunSynchronously();
            return started.Shared;
        }

        private static bool Mutates(HttpRequestMessage request)
        {
            return request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options &&
            !(Upstream(request.RequestUri) == "auth" && request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal));
        }

        public static string Upstream(Uri? uri)
        {
            return uri?.Host switch
            {
                { } host when host == TsAuthOrigin.Value.Host => "auth",
                "api.stripe.com" => "stripe",
                "127.0.0.1" or "localhost" or "::1" => "state",
                { } host when host.EndsWith("api.terrascale.tech", StringComparison.Ordinal) => "terrabase",
                _ => "other",
            };
        }

        public void Record(string upstream, double ms)
        {
            lock (gate)
            {
                var (calls, total) = upstreams.GetValueOrDefault(upstream);
                upstreams[upstream] = (calls + 1, total + ms);
            }
        }

        private void Record(string upstream, double start, double end)
        {
            Record(upstream, end - start);
            lock (gate)
            {
                spans.Add((upstream, start, end));
            }
        }

        private double Wall(string? upstream = null)
        {
            double total = 0, end = 0;
            foreach (var span in spans.Where(span => upstream is null || span.Upstream == upstream).OrderBy(span => span.Start))
            {
                total += Math.Max(0, span.End - Math.Max(end, span.Start));
                end = Math.Max(end, span.End);
            }
            return total;
        }

        public string Header()
        {
            var parts = new List<string> { Metric("app", clock.Elapsed.TotalMilliseconds, "") };
            lock (gate)
            {
                parts.Add(Metric("local", Math.Max(0, clock.Elapsed.TotalMilliseconds - Wall()), "render and application work outside upstream waits"));
                foreach (var entry in upstreams.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    parts.Add(Metric(entry.Key, entry.Value.Ms, entry.Value.Calls + " calls"));
                    parts.Add(Metric(entry.Key + "_wall", Wall(entry.Key), "overlapping calls counted once"));
                }
            }
            return string.Join(", ", parts);
        }

        private static string Metric(string name, double ms, string description)
        {
            return name + ";dur=" + ms.ToString("0.0", CultureInfo.InvariantCulture) + (description.Length == 0 ? "" : ";desc=\"" + description + "\"");
        }

        public static async Task MiddlewareAsync(HttpContext context, Func<Task> next, ILogger logger)
        {
            var timing = new ServerTiming
            {
                reads = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method),
                logger = logger,
                probe = context.Request.Path == "/readyz" || context.Request.Path == "/health/ready"
            };
            Current.Value = timing;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["Server-Timing"] = timing.Header();
                return Task.CompletedTask;
            });
            int? failureStatus = null;
            try { await next(); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                failureStatus = StatusCodes.Status499ClientClosedRequest;
                throw;
            }
            finally
            {
                if (!timing.probe && logger.IsEnabled(LogLevel.Information))
                {
                    logger.PageTiming(SafeHttpLogging.Method(context.Request.Method),
                        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "[unmatched]", failureStatus ?? context.Response.StatusCode, timing.Header());
                }
            }
        }

        public static async Task<T> RpcAsync<T>(string operation, bool mutation, Func<Task<T>> call)
        {
            var timing = Current.Value;
            if (timing is null)
            {
                return await call();
            }

            if (mutation)
            {
                timing.BeginWrite();
            }
            var started = timing.clock.Elapsed.TotalMilliseconds;
            var status = 0;
            try
            {
                var result = await call();
                status = 200;
                return result;
            }
            finally
            {
                if (mutation)
                {
                    timing.EndWrite();
                }
                var ended = timing.clock.Elapsed.TotalMilliseconds;
                timing.Record("terrabase", started, ended);
                var level = UpstreamLevel(status);
                if (!timing.probe && timing.logger is { } logger && logger.IsEnabled(level))
                {
                    logger.UpstreamTimingStartMsDurationMs(level, "terrabase", "rpc", "RPC", operation, status, started, ended - started);
                }
            }
        }

        private static LogLevel UpstreamLevel(int status)
        {
            return status is 0 or 401 or 403 or 429 or >= 500 ? LogLevel.Information : LogLevel.Debug;
        }

        public static async Task<T> StateWriteAsync<T>(Func<Task<T>> write)
        {
            ArgumentNullException.ThrowIfNull(write);
            var timing = Current.Value;
            timing?.BeginWrite();
            try
            {
                return await write();
            }
            finally
            {
                timing?.EndWrite();
            }
        }

        public static async Task StateWriteAsync(Func<Task> write)
        {
            ArgumentNullException.ThrowIfNull(write);
            var timing = Current.Value;
            timing?.BeginWrite();
            try
            {
                await write();
            }
            finally
            {
                timing?.EndWrite();
            }
        }

        private void BeginWrite()
        {
            Interlocked.Increment(ref pendingWrites);
            Interlocked.Increment(ref writes);
        }

        private void EndWrite()
        {
            Interlocked.Increment(ref writes);
            Interlocked.Decrement(ref pendingWrites);
        }

        public sealed class Handler : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var timing = Current.Value;
                if (timing is null)
                {
                    return await base.SendAsync(request, cancellationToken);
                }

                var mutation = Mutates(request);
                if (mutation)
                {
                    timing.BeginWrite();
                }
                var started = timing.clock.Elapsed.TotalMilliseconds;
                HttpResponseMessage? response = null;
                try { return response = await base.SendAsync(request, cancellationToken); }
                finally
                {
                    if (mutation)
                    {
                        timing.EndWrite();
                    }
                    var ended = timing.clock.Elapsed.TotalMilliseconds;
                    var upstream = Upstream(request.RequestUri);
                    timing.Record(upstream, started, ended);
                    var operation = Operation(request.RequestUri);
                    var status = response is null ? 0 : (int)response.StatusCode;
                    var level = UpstreamLevel(status);
                    if (!timing.probe && timing.logger is { } logger && logger.IsEnabled(level))
                    {
                        logger.UpstreamTimingStartMsDurationMs(level, upstream, upstream == "other" ? "[other]" : request.RequestUri?.Host, SafeHttpLogging.Method(request.Method.Method), operation,
                        status, started, ended - started);
                    }
                }
            }

            internal static string Operation(Uri? uri)
            {
                var parts = (uri?.AbsolutePath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
                var identifier = false;
                for (var index = 0; index < parts.Length; index++)
                {
                    var part = parts[index];
                    parts[index] = identifier || !Operations.Contains(part) ? ":value" : part;
                    identifier = !identifier && IdentifiedResources.Contains(part);
                }
                return string.Join('/', parts);
            }

            private static readonly HashSet<string> IdentifiedResources = new(StringComparer.Ordinal)
            { "organizations", "projects", "databases", "members", "invitations" };

            private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
            {
                "terrascale", "v1", "v2", ".well-known", "openid-configuration", "jwks", "oauth", "token", "userinfo",
                "platform", "organizations", "projects", "members", "ownership-transfer", "invitations", "audit-events",
                "databases", "query", "sql", "analytics", "usage", "regions",
                "admin", "plan", "sessions", "accounts", "identities", "allowance_notifications", "get", "list", "transactions"
            };
        }
    }
}
