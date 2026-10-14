using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraScale.DotNet.Tests.TerraBase.Shapes;

/// <summary>Reads the pinned shape conformance vectors copied next to the test binary.</summary>
internal static class ShapeVectors
{
    public const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    public const string Database = "demo";
    public const string Collection = "notes";
    public const string Origin = "https://terrabase.example.test";
    public const string ShapePath = $"/v1/projects/{Project}/databases/{Database}/collections/{Collection}/shape";
    public const string B0 = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
    public const string B1 = "01ARZ3NDEKTSV4RRFFQ69G5FB1";

    private static readonly Lazy<JsonDocument> Fixture = new(() => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contracts", "terrabase", "fixtures", "shape-vectors.json"))));

    public static JsonElement Root => Fixture.Value.RootElement;
    public static string SourceHandle => Root.GetProperty("fixture").GetProperty("handle").GetString()!;
    public static string SourceShapeId => Root.GetProperty("fixture").GetProperty("shape_id").GetString()!;
    public static string DestinationHandle => Root.GetProperty("fixture").GetProperty("destination").GetProperty("handle").GetString()!;
    public static string DestinationShapeId => Root.GetProperty("fixture").GetProperty("destination").GetProperty("shape_id").GetString()!;

    public static JsonElement Http(string id) => Group("http_vectors", id);
    public static JsonElement Apply(string id) => Group("apply_vectors", id);

    /// <summary>The resume token a vector's request carries, from either transport.</summary>
    public static string RequestToken(string id)
    {
        var request = Http(id).GetProperty("request");
        return request.GetProperty("query").TryGetProperty("cursor", out var cursor) ? cursor.GetString()!
            : request.GetProperty("headers").GetProperty("TB-Shape-Resume").GetString()!;
    }

    /// <summary>The body bytes of an HTTP vector exactly as recorded.</summary>
    public static string BodyText(string id) => Http(id).GetProperty("response").GetProperty("body_utf8").GetString()!;

    public static HttpResponseMessage Response(string id) => Response(Http(id).GetProperty("response"));

    /// <summary>Builds the vector's response: status, its required header subset, and the exact body bytes.</summary>
    public static HttpResponseMessage Response(JsonElement response, string? bodyOverride = null)
    {
        var message = new HttpResponseMessage((HttpStatusCode)response.GetProperty("status").GetInt32());
        var body = bodyOverride ?? (response.TryGetProperty("body_utf8", out var text) ? text.GetString() : null);
        message.Content = new ByteArrayContent(body is null ? [] : Encoding.UTF8.GetBytes(body));
        foreach (var header in response.GetProperty("headers").EnumerateObject())
        {
            var value = header.Value.GetString()!;
            if (header.Name == "Content-Type") message.Content.Headers.TryAddWithoutValidation("Content-Type", value);
            else if (header.Name == "Location") message.Headers.Location = new Uri(value, UriKind.RelativeOrAbsolute);
            else message.Headers.TryAddWithoutValidation(header.Name, value);
        }
        return message;
    }

    /// <summary>A sealed-object style <c>200</c> carrying <paramref name="body"/>.</summary>
    public static HttpResponseMessage Ok(string body, string handle, string nextOffset, bool upToDate, string? liveCursor = null)
    {
        var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        message.Headers.TryAddWithoutValidation("Cache-Control", "private, max-age=0, must-revalidate");
        message.Headers.TryAddWithoutValidation("Vary", "Authorization");
        message.Headers.TryAddWithoutValidation("TB-Shape-Handle", handle);
        message.Headers.TryAddWithoutValidation("TB-Shape-Next-Offset", nextOffset);
        message.Headers.TryAddWithoutValidation("TB-Shape-Up-To-Date", upToDate ? "true" : "false");
        if (liveCursor is not null) message.Headers.TryAddWithoutValidation("TB-Shape-Cursor", liveCursor);
        return message;
    }

    /// <summary>A bootstrap <c>307</c> to a snapshot's first page.</summary>
    public static HttpResponseMessage Redirect(string location, string handle, bool resume = false)
    {
        var message = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
        message.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        message.Headers.TryAddWithoutValidation("Cache-Control", resume ? "no-store" : "private, max-age=0, must-revalidate");
        message.Headers.TryAddWithoutValidation("TB-Shape-Handle", handle);
        return message;
    }

    /// <summary>A shape body in the section 6 member order; <paramref name="changes"/> is raw JSON.</summary>
    public static string Body(string shapeId, string handle, string offset, string nextOffset, string covered, bool upToDate, string cursorResume, string changes) =>
        $"{{\"shape_id\":\"{shapeId}\",\"handle\":\"{handle}\",\"offset\":\"{offset}\",\"next_offset\":\"{nextOffset}\",\"covered_offset\":\"{covered}\"," +
        $"\"values_at\":\"{covered}\",\"up_to_date\":{(upToDate ? "true" : "false")},\"cursor_resume\":\"{cursorResume}\",\"changes\":{changes}}}";

    /// <summary>A one-page snapshot <c>S</c> whose upserts recreate <paramref name="state"/> (an apply vector state object).</summary>
    public static string Snapshot(string shapeId, string handle, string snapshot, string cursorResume, JsonElement state)
    {
        var ops = new JsonArray();
        foreach (var document in state.EnumerateObject())
        {
            ops.Add(new JsonObject
            {
                ["op"] = "upsert",
                ["id"] = document.Name,
                ["version"] = document.Value.GetProperty("version").GetString(),
                ["document"] = JsonNode.Parse(document.Value.GetProperty("document").GetRawText()),
            });
        }
        var changes = new JsonArray { new JsonObject { ["offset"] = snapshot, ["ops"] = ops } };
        return Body(shapeId, handle, $"snap.{snapshot}.0", snapshot, snapshot, false, cursorResume, changes.ToJsonString());
    }

    /// <summary>A fresh, empty, up-to-date body at decimal <paramref name="offset"/>.</summary>
    public static string Fresh(string shapeId, string handle, string offset, string cursorResume) =>
        Body(shapeId, handle, offset, offset, offset, true, cursorResume, "[]");

    private static JsonElement Group(string group, string id) =>
        Root.GetProperty(group).EnumerateArray().Single(vector => vector.GetProperty("id").GetString() == id);
}

/// <summary>One request the fake TerraBase received, with its query split exactly as sent.</summary>
internal sealed record ShapeRequest(Uri Uri, IReadOnlyList<KeyValuePair<string, string>> Pairs, string? Authorization, IReadOnlyList<string> Resume, string? CacheControl)
{
    public IReadOnlyDictionary<string, string> Query { get; } = Pairs.GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value);
    public string? Handle => Query.GetValueOrDefault("handle");
    public string? Offset => Query.GetValueOrDefault("offset");
    public string? Cursor => Query.GetValueOrDefault("cursor");
    public string? Where => Query.GetValueOrDefault("where");
    public bool Live => Query.GetValueOrDefault("live") == "true";
    public bool IsResume => Offset == "-1" && (Cursor is not null || Resume.Count != 0);
    public bool IsPlainBootstrap => Offset == "-1" && Cursor is null && Resume.Count == 0 && Handle is null;

    public static ShapeRequest From(HttpRequestMessage request)
    {
        var raw = request.RequestUri!.Query.TrimStart('?');
        var pairs = raw.Length == 0 ? [] : raw.Split('&').Select(part =>
        {
            var split = part.Split('=', 2);
            return KeyValuePair.Create(Uri.UnescapeDataString(split[0]), Uri.UnescapeDataString(split.Length == 2 ? split[1] : string.Empty));
        }).ToList();
        var resume = request.Headers.TryGetValues("TB-Shape-Resume", out var values) ? values.ToList() : [];
        return new ShapeRequest(request.RequestUri, pairs, request.Headers.Authorization?.ToString(), resume, request.Headers.CacheControl?.ToString());
    }
}

/// <summary>
/// A scripted TerraBase: each request goes to <c>route</c>; nothing leaves the process. It stops a looping client
/// after a fixed number of requests and records whether the caller-owned handler was ever disposed.
/// </summary>
internal sealed class ShapeFakeServer(Func<ShapeRequest, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
{
    private const int MaxRequests = 64;
    private readonly List<ShapeRequest> requests = [];

    public ShapeFakeServer(Func<ShapeRequest, HttpResponseMessage> route) : this((request, _) => Task.FromResult(route(request))) { }

    public bool Disposed { get; private set; }

    public IReadOnlyList<ShapeRequest> Requests
    {
        get { lock (requests) return [.. requests]; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = ShapeRequest.From(request);
        lock (requests)
        {
            requests.Add(recorded);
            if (requests.Count > MaxRequests) throw new InvalidOperationException("The shape client kept requesting; it is looping.");
        }
        return await route(recorded, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    /// <summary>Holds a request open until the caller cancels it, recording that it was reached.</summary>
    public static async Task<HttpResponseMessage> Hold(TaskCompletionSource reached, CancellationToken cancellationToken)
    {
        reached.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>
/// A clock that moves only when a test advances it, so backoff and Retry-After are observed without waiting.
/// </summary>
internal sealed class ShapeManualClock : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (timers) return now;
    }

    public override long GetTimestamp()
    {
        lock (timers) return now.UtcTicks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Due times of timers still armed, relative to now.</summary>
    public IReadOnlyList<TimeSpan> Pending
    {
        get
        {
            lock (timers) return [.. timers.Where(timer => timer.Due is not null).Select(timer => timer.Due!.Value - now)];
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (timers)
        {
            now += by;
            due = [.. timers.Where(timer => timer.Due <= now)];
            foreach (var timer in due) timer.Due = null;
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ShapeManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.timers)
            {
                clock.timers.Remove(this);
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                if (Due is not null) clock.timers.Add(this);
            }
            if (dueTime == TimeSpan.Zero) Fire();
            return true;
        }

        public void Fire()
        {
            lock (clock.timers) clock.timers.Remove(this);
            callback(state);
        }

        public void Dispose()
        {
            lock (clock.timers) clock.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
