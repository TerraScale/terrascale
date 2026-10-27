using System.Diagnostics;
using System.Globalization;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Platform.Http;

// Per-request time spent waiting on each upstream, so a slow page names the dependency it waited on.
// Browsers show the Server-Timing header in their network panel; the log line keeps prod regressions visible.
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

    // A point in this request's upstream history: it stays current until the request sends something that can change
    // state (a state write, or a ts-auth or Stripe mutation), so a value read before it is still as fresh as a re-read.
    public sealed record Mark(ServerTiming Request, int Writes)
    {
        public bool Current => ServerTiming.Current.Value == Request && Volatile.Read(ref Request.writes) == Writes && Volatile.Read(ref Request.pendingWrites) == 0;
    }

    // A freshly authorized context also serves a form action until that action sends a write.
    // Individual read memoization below remains limited to GET/HEAD.
    public static Mark? Now() => Current.Value is { pendingWrites: 0 } timing ? new(timing, Volatile.Read(ref timing.writes)) : null;

    // Coalesce identical reads only within this GET/HEAD. A mutation invalidates earlier reads;
    // failed reads can be retried and no task or authority survives into another request.
    public static Task<T> ReadAsync<T>(object key, Func<Task<T>> read)
        => ReadAsync(key, read, false);

    // Public issuer metadata cannot change because this request wrote account or document
    // state. Share it through those writes, but never into the next request (key rotation).
    public static Task<T> PublicReadAsync<T>(object key, Func<Task<T>> read)
        => ReadAsync(key, read, true);

    private static Task<T> ReadAsync<T>(object key, Func<Task<T>> read, bool publicMetadata)
    {
        if (Current.Value is not { reads: true } timing) return read();
        lock (timing.gate)
        {
            if (!publicMetadata && Volatile.Read(ref timing.pendingWrites) != 0) return read();
            var generation = publicMetadata ? -1 : Volatile.Read(ref timing.writes);
            if (timing.readTasks.TryGetValue(key, out var entry) && entry.Writes == generation &&
                entry.Task is Task<T> pending && !pending.IsFaulted && !pending.IsCanceled) return pending;
            var task = StartAsync();
            timing.readTasks[key] = (generation, task);
            return task;
        }
        async Task<T> StartAsync() => await read();
    }

    // Token grants and publishing storage allowances change none of the account/permission/billing
    // records the dashboard context reads. All other mutations still invalidate it, including failed writes.
    private static bool Mutates(HttpRequestMessage request) =>
        request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options &&
        !(Upstream(request.RequestUri) == "auth" && request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal)) &&
        !PublishesPlan(request);

    private static bool PublishesPlan(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Put || Upstream(request.RequestUri) != "state") return false;
        return request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is
            ["admin", "organizations" or "projects", var id, "plan"] && TerraScaleIds.IsValid(id);
    }

    // Upstream names by host: ts-auth, TerraBase's public API, the dashboard's own state database, Stripe.
    public static string Upstream(Uri? uri) => uri?.Host switch
    {
        "auth.terrascale.tech" => "auth",
        "api.stripe.com" => "stripe",
        "127.0.0.1" or "localhost" or "::1" => "state",
        { } host when host.EndsWith("api.terrascale.tech", StringComparison.Ordinal) => "terrabase",
        _ => "other",
    };

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
        lock (gate) spans.Add((upstream, start, end));
    }

    // Union of intervals measures elapsed dependency time without counting concurrent calls twice.
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

    // Calls that overlap add up, so an upstream's total can exceed the page's own time when they ran in parallel.
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

    private static string Metric(string name, double ms, string description) =>
        name + ";dur=" + ms.ToString("0.0", CultureInfo.InvariantCulture) + (description.Length == 0 ? "" : ";desc=\"" + description + "\"");

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
        try { await next(); }
        finally
        {
            bool called;
            lock (timing.gate) called = timing.upstreams.Count > 0;
            if (context.GetEndpoint() is not null && called)
                logger.LogInformation("Page timing {Method} {Route} {Status}: {Timing}", SafeHttpLogging.Method(context.Request.Method),
                    (context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? "[unmatched]", context.Response.StatusCode, timing.Header());
        }
    }

    // Times every request sent through a dashboard HttpClient into the current page's record.
    public sealed class Handler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var timing = Current.Value;
            if (timing is null) return await base.SendAsync(request, cancellationToken);
            var mutation = Mutates(request);
            if (mutation)
            {
                Interlocked.Increment(ref timing.pendingWrites);
                Interlocked.Increment(ref timing.writes);
            }
            var started = timing.clock.Elapsed.TotalMilliseconds;
            HttpResponseMessage? response = null;
            try { return response = await base.SendAsync(request, cancellationToken); }
            finally
            {
                if (mutation)
                {
                    Interlocked.Increment(ref timing.writes);
                    Interlocked.Decrement(ref timing.pendingWrites);
                }
                var ended = timing.clock.Elapsed.TotalMilliseconds;
                var upstream = Upstream(request.RequestUri);
                timing.Record(upstream, started, ended);
                // Only fixed operation names escape: no URL, query, token, document ID or customer name.
                var operation = Operation(request.RequestUri);
                if (!timing.probe) timing.logger?.LogInformation("Upstream timing {Upstream} {Host} {Method} {Operation} {Status}: start {StartMs} ms, duration {DurationMs} ms",
                    upstream, upstream == "other" ? "[other]" : request.RequestUri?.Host, SafeHttpLogging.Method(request.Method.Method), operation,
                    response is null ? 0 : (int)response.StatusCode, started, ended - started);
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
        { "organizations", "projects", "databases", "collections", "documents", "members", "invitations" };

        private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
        {
            "terrascale", "v1", "v2", ".well-known", "openid-configuration", "jwks", "oauth", "token", "userinfo",
            "platform", "organizations", "projects", "members", "ownership-transfer", "invitations", "audit-events",
            "databases", "collections", "documents", "query", "sql", "analytics", "usage", "replication", "regions",
            "admin", "plan", "sessions", "accounts", "identities", "allowance_notifications", "get", "list", "transactions"
        };
    }
}
