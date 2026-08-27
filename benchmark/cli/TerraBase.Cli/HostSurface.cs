// <copyright file="HostSurface.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// The host-mode surface: the API host's typed HTTP surface (SE-08),
/// driven with the backend-supplied context the host's local gate
/// checks (PD-2026-08-26-11 D1 — the CLI is a local tool without an
/// auth gate; the labels are context, never credentials, and are
/// never logged). Every operation is one typed call; the SSE
/// projection of the Change Event Backbone is the only long-lived
/// request.
/// </summary>
public sealed class HostSurface : ITerraBaseSurface
{
    private readonly CliConfiguration configuration;
    private readonly HttpClient http;
    private readonly bool ownsClient;

    /// <summary>Initializes the surface over its own client.</summary>
    /// <param name="configuration">The resolved configuration.</param>
    public HostSurface(CliConfiguration configuration)
    {
        this.configuration = configuration;
        this.ownsClient = true;
        this.http = NewClient();
        this.http.DefaultRequestHeaders.Add("X-TB-Principal", configuration.Principal);
        this.http.DefaultRequestHeaders.Add("X-TB-Tenant", configuration.Tenant);
    }

    private HostSurface(CliConfiguration configuration, HttpClient http)
    {
        this.configuration = configuration;
        this.ownsClient = false;
        this.http = http;
        this.http.DefaultRequestHeaders.Add("X-TB-Principal", configuration.Principal);
        this.http.DefaultRequestHeaders.Add("X-TB-Tenant", configuration.Tenant);
    }

    /// <inheritdoc/>
    public async Task<StatusReport> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var health = await this.GetJsonAsync("/health", allowUnavailable: true, cancellationToken).ConfigureAwait(false);
        var engine = health.GetProperty("engine");
        var report = new StatusReport(
            HealthStatus: health.GetProperty("status").GetString() == "serving" ? "serving" : "unavailable",
            EngineState: StringOf(engine, "state"),
            Sessions: Number32Of(engine, "sessions"),
            Subscriptions: Number32Of(engine, "subscriptions"),
            FramesServed: Number64Of(engine, "framesServed"),
            Generation: engine.TryGetProperty("generation", out var generation) && generation.ValueKind == JsonValueKind.Number ? generation.GetInt32() : 1,
            Restarts: engine.TryGetProperty("restarts", out var restarts) && restarts.ValueKind == JsonValueKind.Number ? restarts.GetInt32() : 0,
            Detail: StringOf(engine, "detail"),
            RegistryVersion: null,
            Scopes: null,
            Binding: null,
            Subsystems: null,
            EngineTopologyAvailable: null);
        if (report.HealthStatus != "serving")
        {
            // Truthful and bounded: an unavailable engine is reported as
            // exactly that; no sub-read runs against a lost generation.
            return report;
        }

        var admin = await this.GetJsonAsync("/v1/engine/status", allowUnavailable: false, cancellationToken).ConfigureAwait(false);
        var topology = await this.GetJsonAsync("/v1/engine/topology", allowUnavailable: false, cancellationToken).ConfigureAwait(false);
        return report with
        {
            RegistryVersion = StringOf(admin, "registryVersion"),
            Scopes = Number32Of(admin, "scopes"),
            Binding = topology.TryGetProperty("binding", out var binding) ? StringOf(binding, "variant") : null,
            Subsystems = topology.TryGetProperty("subsystems", out var subsystems) && subsystems.ValueKind == JsonValueKind.Array
                ? [.. subsystems.EnumerateArray().Select(item => item.GetString() ?? string.Empty)]
                : null,
            EngineTopologyAvailable = BoolOf(topology, "engineTopologyAvailable"),
        };
    }

    /// <inheritdoc/>
    public async Task<DocumentReadOutcome> ReadDocumentAsync(string collection, string document, CancellationToken cancellationToken)
    {
        var answer = await this.GetJsonAsync($"/v1/documents/{collection}/{document}", allowUnavailable: false, cancellationToken).ConfigureAwait(false);
        if (!answer.GetProperty("found").GetBoolean())
        {
            return new DocumentReadOutcome(false, null, null, null, null);
        }

        return DocumentOf(answer.GetProperty("document"));
    }

    /// <inheritdoc/>
    public async Task<WriteOutcome> WriteDocumentAsync(string collection, string document, JsonElement body, string? expected, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(Values.ToDiscriminated(body)), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/documents/{collection}/{document}") { Content = content };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        AddExpected(request, expected);
        return WriteOutcomeOf(await this.SendJsonAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task<WriteOutcome> DeleteDocumentAsync(string collection, string document, string? expected, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/documents/{collection}/{document}");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        AddExpected(request, expected);
        return WriteOutcomeOf(await this.SendJsonAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteQueryAsync(string text, uint maxItems, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { text, maxItems }), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/query") { Content = content };
        var answer = await this.SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
        var documents = answer.GetProperty("documents").EnumerateArray().Select(DocumentOf).ToArray();
        return new QueryOutcome(documents, answer.GetProperty("count").GetUInt32(), answer.GetProperty("terminal").GetString() ?? "unknown");
    }

    /// <inheritdoc/>
    public async Task<string> CancelQueryAsync(ulong queryId, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { queryId }), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/query/cancel") { Content = content };
        var answer = await this.SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
        return answer.GetProperty("outcome").GetString() ?? "unknown";
    }

    /// <inheritdoc/>
    public async Task<IChangesTail> OpenChangesAsync(ulong from, string[]? collections, CancellationToken cancellationToken)
    {
        var query = new StringBuilder($"?from={from}");
        if (collections is { Length: > 0 } filter)
        {
            query.Append("&collections=").Append(string.Join(",", filter));
        }

        // The tail's own request: the headers arrive, then the event
        // stream reads unbounded — the follow loop owns the timing.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/streams{query}");
        HttpResponseMessage response;
        try
        {
            response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SurfaceUnavailableException($"the API host at {this.configuration.Url} did not answer within its bound");
        }
        catch (Exception failure) when (failure is HttpRequestException or SocketException or InvalidOperationException)
        {
            throw new SurfaceUnavailableException($"the API host at {this.configuration.Url} cannot be reached: {failure.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw Typed(response.StatusCode, body, "the Change Event Backbone refused the subscription");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new HostChangesTail(this.http, response, new StreamReader(stream, Encoding.UTF8));
    }

    /// <inheritdoc/>
    public async Task<SessionsReport> ReadSessionsAsync(CancellationToken cancellationToken)
    {
        var answer = await this.GetJsonAsync("/v1/sync/sessions", allowUnavailable: false, cancellationToken).ConfigureAwait(false);
        (ulong SessionId, string Collection, int Generation)[] sessions = answer.ValueKind == JsonValueKind.Array
            ? [.. answer.EnumerateArray().Select(item => (
                item.GetProperty("sessionId").GetUInt64(),
                item.GetProperty("collection").GetString() ?? string.Empty,
                item.GetProperty("generation").GetInt32()))]
            : [];
        return new SessionsReport(true, sessions, null);
    }

    /// <inheritdoc/>
    public async Task<JsonElement> ReadAuditAsync(int limit, CancellationToken cancellationToken) =>
        (await this.GetJsonAsync($"/v1/audit?limit={limit}", allowUnavailable: false, cancellationToken).ConfigureAwait(false)).Clone();

    /// <inheritdoc/>
    public async Task<JsonElement> ReadMetricsAsync(CancellationToken cancellationToken) =>
        (await this.GetJsonAsync("/v1/metrics", allowUnavailable: false, cancellationToken).ConfigureAwait(false)).Clone();

    /// <inheritdoc/>
    public async Task<ShutdownReport> ShutdownAsync(string reason, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { reason }), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/engine/shutdown") { Content = content };
        var answer = await this.SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
        return new ShutdownReport(answer.GetProperty("state").GetString() ?? "unknown", answer.GetProperty("inFlight").GetUInt32());
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (this.ownsClient)
        {
            this.http.Dispose();
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    internal HttpClient NewClient()
    {
        if (CliTransport.HandlerFactory is { } factory)
        {
            var injected = factory();
            injected.BaseAddress = new Uri(this.configuration.Url);
            return injected;
        }

        return new HttpClient { BaseAddress = new Uri(this.configuration.Url), Timeout = TimeSpan.FromSeconds(30) };
    }

    private static void AddExpected(HttpRequestMessage request, string? expected)
    {
        if (expected is not null)
        {
            request.Headers.Add("X-TB-Expected", expected);
        }
    }

    private static DocumentReadOutcome DocumentOf(JsonElement body) => new(
        true,
        StringOf(body, "id"),
        StringOf(body, "collection"),
        Number64Of(body, "version"),
        Values.ToPlain(body.GetProperty("body")));

    private static WriteOutcome WriteOutcomeOf(JsonElement answer)
    {
        var outcome = answer.GetProperty("outcome").GetString() ?? "unknown";
        if (answer.TryGetProperty("receipt", out var receipt) && receipt.ValueKind == JsonValueKind.Object)
        {
            return new WriteOutcome(outcome, Number64Of(receipt, "sequence"), Number64Of(receipt, "version"), StringOf(receipt, "operationId"), null);
        }

        return new WriteOutcome(outcome, null, null, null, StringOf(answer, "reason"));
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ulong? Number64Of(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt64() : null;

    private static uint? Number32Of(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt32() : null;

    private static bool? BoolOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private async Task<JsonElement> GetJsonAsync(string path, bool allowUnavailable, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await this.SendJsonAsync(request, cancellationToken, allowUnavailable).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool allowUnavailable = false)
    {
        HttpResponseMessage response;
        try
        {
            response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SurfaceUnavailableException($"the API host at {this.configuration.Url} did not answer within its bound");
        }
        catch (Exception failure) when (failure is HttpRequestException or SocketException or InvalidOperationException)
        {
            throw new SurfaceUnavailableException($"the API host at {this.configuration.Url} cannot be reached: {failure.Message}");
        }

        using var owned = response;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && !(allowUnavailable && response.StatusCode == HttpStatusCode.ServiceUnavailable))
        {
            throw Typed(response.StatusCode, body, "the host refused the operation");
        }

        try
        {
            return JsonDocument.Parse(body.Length > 0 ? body : "{}").RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SurfaceUnavailableException($"the API host at {this.configuration.Url} answered an unparseable body");
        }
    }

    private static Exception Typed(HttpStatusCode status, string body, string fallback)
    {
        try
        {
            var error = JsonDocument.Parse(body.Length > 0 ? body : "{}").RootElement;
            var code = StringOf(error, "code") ?? $"http_{(int)status}";
            var detail = StringOf(error, "detail") ?? fallback;
            return code is "engine-unavailable" or "unavailable"
                ? new SurfaceUnavailableException(detail)
                : new SurfaceRefusalException(code, detail);
        }
        catch (JsonException)
        {
            return new SurfaceUnavailableException($"the host answered status {(int)status} without a typed error");
        }
    }

    /// <summary>
    /// The SSE tail: one backbone subscription's server-sent events,
    /// expanded into the mode-agnostic tail frames (a batch event
    /// becomes its ordered change events; a trigger becomes the
    /// content-free Change Notification).
    /// </summary>
    internal sealed class HostChangesTail(HttpClient http, HttpResponseMessage response, StreamReader reader) : IChangesTail
    {
        private readonly Queue<TailFrame> pending = new();
        private ulong subscriptionId;
        private bool ended;

        /// <inheritdoc/>
        public async Task<TailFrame?> ReadAsync(CancellationToken cancellationToken)
        {
            while (this.pending.Count == 0 && !this.ended)
            {
                var frame = await this.ReadEventAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    this.ended = true;
                    return null;
                }

                this.Expand(frame.Value.name, frame.Value.id, frame.Value.data);
            }

            return this.pending.Count > 0 ? this.pending.Dequeue() : null;
        }

        /// <inheritdoc/>
        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            if (this.subscriptionId == 0)
            {
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/streams/{this.subscriptionId}");
                using var answer = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _ = answer;
            }
            catch (Exception failure) when (failure is HttpRequestException or SocketException or InvalidOperationException or OperationCanceledException)
            {
                // The bounded close is best effort: the host closes the
                // stream on consumer-disconnected regardless.
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            reader.Dispose();
            response.Dispose();
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        private async Task<(string name, string? id, string data)?> ReadEventAsync(CancellationToken cancellationToken)
        {
            string? line;
            string? id = null;
            string? name = null;
            var data = new StringBuilder();
            while ((line = await this.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    if (name is not null)
                    {
                        return (name, id, data.ToString());
                    }

                    continue;
                }

                if (line.StartsWith("id:", StringComparison.Ordinal))
                {
                    id = line[3..].Trim();
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    name = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.AppendLine(line[5..].TrimStart());
                }
            }

            return null;
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or SocketException or ObjectDisposedException)
            {
                throw new ConnectionDroppedException(failure.Message);
            }
        }

        private void Expand(string name, string? id, string data)
        {
            using var payload = JsonDocument.Parse(data.Length > 0 ? data : "{}");
            var root = payload.RootElement;
            switch (name)
            {
                case "opened":
                    this.subscriptionId = Number64Of(root, "subscriptionId") ?? 0;
                    this.pending.Enqueue(new TailFrame("opened", Number64Of(root, "startSequence"), null, null, null, null, null, null, null));
                    break;
                case "batch":
                    var next = Number64Of(root, "nextSequence");
                    if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var change in events.EnumerateArray())
                        {
                            this.pending.Enqueue(new TailFrame(
                                "change-event",
                                Number64Of(change, "sequence"),
                                StringOf(change, "kind"),
                                StringOf(change, "collection"),
                                StringOf(change, "document"),
                                StringOf(change, "operationId"),
                                null,
                                null,
                                next));
                        }
                    }

                    break;
                case "trigger":
                    this.pending.Enqueue(new TailFrame(
                        "change-notification",
                        Number64Of(root, "sequence"),
                        null,
                        StringOf(root, "collection"),
                        null,
                        StringOf(root, "operationId"),
                        null,
                        null,
                        null));
                    break;
                case "drop":
                    this.pending.Enqueue(new TailFrame(
                        "drop",
                        null,
                        null,
                        null,
                        null,
                        null,
                        Number64Of(root, "resumeFrom"),
                        null,
                        null));
                    break;
                case "closed":
                    this.pending.Enqueue(new TailFrame(
                        "closed",
                        null,
                        null,
                        null,
                        null,
                        null,
                        Number64Of(root, "resumeFrom"),
                        StringOf(root, "reason"),
                        null));
                    break;
                default:
                    // An unknown event name is ignored, not fatal: the
                    // envelope contract stays stable and versioned.
                    break;
            }
        }
    }
}

/// <summary>The connection dropped under a tail read: the follow loop resumes from its checkpoint.</summary>
/// <param name="detail">The bounded detail.</param>
public sealed class ConnectionDroppedException(string detail) : Exception(detail);
