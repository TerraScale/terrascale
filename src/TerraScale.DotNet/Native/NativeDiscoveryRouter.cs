using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TerraScale.DotNet.Native;

/// <summary>Consumes authenticated, scoped Atlas admission without catalog or origin fallback.</summary>
public sealed class NativeDiscoveryRouter : INativeAdmissionProvider, IDisposable
{
    private const int BodyLimit = 64 * 1024;
    private const int ScopeLimit = 256;
    private static readonly string[] CapabilityNames =
        ["client_v1", "read", "query", "subscriptions", "write", "conditional_mutation", "atomic_groups", "security_state"];
    private static readonly HashSet<string> DiscoveryErrorCodes = new(StringComparer.Ordinal)
    {
        "invalid_request", "invalid_scope", "invalid_cursor", "discovery_unconfigured",
        "discovery_forbidden", "no_eligible_region", "routing_observation_regressed"
    };
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Uri atlasOrigin;
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<NativeScope, Floor> floors = [];
    private int disposed;

    /// <summary>Creates an owned discovery transport; a supplied handler transfers disposal ownership.</summary>
    /// <remarks>The handler must disable redirects, cookies and decompression; do not share or dispose it separately.</remarks>
    public NativeDiscoveryRouter(TimeSpan timeout, HttpMessageHandler? handler = null, TimeProvider? clock = null)
        : this(NativeTransport.CreateClient(handler), timeout, atlasOrigin: null, clock, ownsClient: true)
    {
    }

    /// <summary>Uses authenticated discovery over a caller-owned HttpClient without changing its configuration.</summary>
    /// <remarks>The configured Atlas origin is trusted by the caller; no serving or API origin is inferred. Supplied handlers must disable redirects, cookies and decompression.</remarks>
    public NativeDiscoveryRouter(HttpClient httpClient, TimeSpan timeout, Uri? atlasOrigin = null, TimeProvider? clock = null)
        : this(httpClient, timeout, atlasOrigin, clock, ownsClient: false)
    {
    }

    private NativeDiscoveryRouter(HttpClient httpClient, TimeSpan timeout, Uri? atlasOrigin, TimeProvider? clock, bool ownsClient)
    {
        try
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
                throw new ArgumentOutOfRangeException(nameof(timeout), "native discovery timeout must be positive and at most 30 seconds");
            client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.atlasOrigin = NativeAtlasOrigin.Validate(atlasOrigin ?? new Uri("https://atlas.terrascale.tech"));
            this.timeout = timeout;
            this.clock = clock ?? TimeProvider.System;
            this.ownsClient = ownsClient;
        }
        catch
        {
            if (ownsClient) httpClient?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<NativeAdmission> ResolveAsync(
        NativeScope scope,
        string apiKey,
        IReadOnlyCollection<string> capabilities,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateScope(scope);
        ValidateCredential(apiKey);
        if (capabilities.Count == 0 || capabilities.Any(capability => !CapabilityNames.Contains(capability, StringComparer.Ordinal)))
            throw new NativeAdmissionException("invalid_capability");

        Floor? observed;
        lock (gate)
        {
            floors.TryGetValue(scope, out observed);
            if (observed is null && floors.Count >= ScopeLimit)
                throw new NativeAdmissionException("routing_capacity");
        }

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant_id"] = scope.Tenant,
            ["database_id"] = scope.Database,
            ["shard_id"] = scope.Shard.ToString(CultureInfo.InvariantCulture)
        };
        if (observed is not null)
        {
            query["observed_deployment_id"] = observed.Authority.Deployment;
            query["observed_cell"] = observed.Authority.Cell;
            query["observed_publisher_generation"] = observed.Authority.Generation.ToString(CultureInfo.InvariantCulture);
            query["rev"] = observed.Revision.ToString(CultureInfo.InvariantCulture);
        }
        var encoded = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(atlasOrigin, "/api/v1/discovery?" + encoded));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var startedAt = clock.GetUtcNow();
        var startedTimestamp = clock.GetTimestamp();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var response = await SendDiscoveryAsync(request, deadline.Token, cancellationToken).ConfigureAwait(false);
        var body = await ReadDiscoveryBodyAsync(response, deadline.Token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new NativeAdmissionException(ReadErrorCode(body), (int)response.StatusCode);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new NativeAdmissionException("invalid_discovery");

        Document document;
        try
        {
            using var json = JsonDocument.Parse(body);
            document = ParseDocument(json.RootElement, scope);
        }
        catch (JsonException)
        {
            throw new NativeAdmissionException("invalid_discovery");
        }

        var lifetime = TimeSpan.FromSeconds(document.RefreshSeconds);
        var remainder = DateTimeOffset.FromUnixTimeSeconds(document.ExpiresAt) - startedAt;
        if (remainder < lifetime) lifetime = remainder;
        var lease = new Lease(document, startedTimestamp, lifetime);
        AssertFresh(lease);
        lock (gate)
        {
            if (floors.TryGetValue(scope, out var current) && !current.Accepts(document))
                throw new NativeAdmissionException("routing_observation_regressed");
            if (!floors.ContainsKey(scope) && floors.Count >= ScopeLimit)
                throw new NativeAdmissionException("routing_capacity");
            floors[scope] = new Floor(document.Authority, document.Revision);
        }

        // Atlas orders eligible direct members by proximity. Operation capabilities
        // further restrict admission; array order never overrides scope or fencing.
        var endpoint = document.Endpoints.FirstOrDefault(endpoint =>
            endpoint.Capabilities.Contains("client_v1") && capabilities.All(endpoint.Capabilities.Contains));
        if (endpoint is null) throw new NativeAdmissionException("capability_unavailable");
        return new NativeAdmission(endpoint.Url, () => AssertCurrent(scope, lease));
    }

    private void AssertCurrent(NativeScope scope, Lease lease)
    {
        AssertFresh(lease);
        lock (gate)
        {
            if (!floors.TryGetValue(scope, out var current) || !current.Accepts(lease.Document))
                throw new NativeAdmissionException("routing_observation_regressed");
        }
    }

    private async Task<HttpResponseMessage> SendDiscoveryAsync(
        HttpRequestMessage request,
        CancellationToken deadlineToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadlineToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new NativeAdmissionException("discovery_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NativeAdmissionException("discovery_unavailable");
        }
    }

    private static async Task<byte[]> ReadDiscoveryBodyAsync(
        HttpResponseMessage response,
        CancellationToken deadlineToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadBodyAsync(response, deadlineToken).ConfigureAwait(false);
            deadlineToken.ThrowIfCancellationRequested();
            return body;
        }
        catch (IOException)
        {
            throw new NativeAdmissionException("discovery_unavailable");
        }
        catch (HttpRequestException)
        {
            throw new NativeAdmissionException("discovery_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NativeAdmissionException("discovery_unavailable");
        }
    }

    private void AssertFresh(Lease lease)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (lease.Lifetime <= TimeSpan.Zero || now < lease.Document.PublishedAt || now >= lease.Document.ExpiresAt ||
            clock.GetElapsedTime(lease.StartedTimestamp) >= lease.Lifetime)
            throw new NativeAdmissionException("discovery_expired");
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > BodyLimit)
            throw new NativeAdmissionException("invalid_discovery");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return body.ToArray();
            if (body.Length + count > BodyLimit) throw new NativeAdmissionException("invalid_discovery");
            body.Write(buffer, 0, count);
        }
    }

    private static Document ParseDocument(JsonElement root, NativeScope expected)
    {
        RequireObject(root, ["schema_version", "scope", "authority", "revision", "published_at", "expires_at", "refreshSeconds", "residency", "endpoints"]);
        if (ReadInt64(root, "schema_version") != 1) Invalid();
        var scope = root.GetProperty("scope");
        RequireObject(scope, ["tenant_id", "database_id", "shard_id"]);
        if (ReadString(scope, "tenant_id") != expected.Tenant || ReadString(scope, "database_id") != expected.Database ||
            ReadU64(scope, "shard_id") != expected.Shard) Invalid();

        var authority = root.GetProperty("authority");
        RequireObject(authority, ["deployment_id", "cell", "publisher_id", "publisher_generation"]);
        var identity = new Authority(ReadString(authority, "deployment_id"), ReadString(authority, "cell"),
            ReadString(authority, "publisher_id"), ReadU64(authority, "publisher_generation"));
        if (!IsIdentity(identity.Deployment) || !IsToken(identity.Cell) || !IsToken(identity.Publisher) || identity.Generation == 0) Invalid();
        var revision = ReadU64(root, "revision");
        var published = ReadInt64(root, "published_at");
        var expires = ReadInt64(root, "expires_at");
        var refresh = ReadInt64(root, "refreshSeconds");
        if (revision == 0 || published < 1 || expires > 253402300799 || expires <= published ||
            expires - published > 10 || refresh is < 1 or > 10) Invalid();

        HashSet<string>? residency = null;
        var residencyValue = root.GetProperty("residency");
        if (residencyValue.ValueKind != JsonValueKind.Null)
        {
            if (residencyValue.ValueKind != JsonValueKind.Array || residencyValue.GetArrayLength() is < 1 or > 20) Invalid();
            residency = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in residencyValue.EnumerateArray())
            {
                var region = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
                if (!NativeRegionalOrigin.IsRegionCode(region) || !residency.Add(region)) Invalid();
            }
        }

        var entries = root.GetProperty("endpoints");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 20) Invalid();
        var endpoints = new List<Endpoint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries.EnumerateArray())
        {
            RequireObject(item, ["region", "url", "catalog", "capabilities", "security_writer", "priority"]);
            var region = ReadString(item, "region");
            var url = ReadString(item, "url");
            if (!seen.Add(region) || !NativeRegionalOrigin.IsTrustedMemberHost(url, region) ||
                ReadInt64(item, "priority") != endpoints.Count + 1 || (residency is not null && !residency.Contains(region))) Invalid();
            var catalog = item.GetProperty("catalog");
            RequireObject(catalog, ["digest"]);
            var digest = ReadString(catalog, "digest");
            if (digest.Length != 64 || digest.Any(character => character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))) Invalid();
            var flags = item.GetProperty("capabilities");
            RequireObject(flags, CapabilityNames);
            var supported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in CapabilityNames)
            {
                var value = flags.GetProperty(name);
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Invalid();
                if (value.GetBoolean()) supported.Add(name);
            }
            if (!supported.Contains("client_v1") || (!supported.Contains("read") && !supported.Contains("write"))) Invalid();
            // The accepted public fixture admits no advanced writer envelope.
            // Its successor must be consumed before claiming these capabilities.
            if (supported.Overlaps(["conditional_mutation", "atomic_groups", "security_state"]) ||
                item.GetProperty("security_writer").ValueKind != JsonValueKind.Null)
                throw new NativeAdmissionException("unsupported_discovery_writer");
            endpoints.Add(new Endpoint(url, supported));
        }
        return new Document(identity, revision, published, expires, (int)refresh, endpoints);
    }

    private static void ValidateScope(NativeScope scope)
    {
        if (!IsUuid(scope.Tenant) || !IsUuid(scope.Database) || scope.Shard == 0)
            throw new NativeAdmissionException("invalid_scope");
    }

    private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out var uuid) &&
        uuid != Guid.Empty && uuid.ToString("D") == value;

    private static void ValidateCredential(string credential)
    {
        if (credential.Length != 71) throw new NativeAdmissionException("invalid_credential");
        var parts = credential.Split('.');
        if (parts.Length != 3 || parts[0] != "tbk1" || !IsBase64Url(parts[1], 16) || !IsBase64Url(parts[2], 32))
            throw new NativeAdmissionException("invalid_credential");
    }

    private static bool IsBase64Url(string value, int bytes)
    {
        if (value.Any(character => character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_')))
            return false;
        try
        {
            var decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
            return decoded.Length == bytes && Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_') == value;
        }
        catch (FormatException) { return false; }
    }

    private static string ReadErrorCode(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            var code = ReadString(error, "code");
            return DiscoveryErrorCodes.Contains(code) ? code : "discovery_http_error";
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "discovery_http_error";
        }
    }

    private static void RequireObject(JsonElement value, IReadOnlyCollection<string> names)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) Invalid();
        if (seen.Count != names.Count) Invalid();
    }

    private static string ReadString(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String) Invalid();
        return property.GetString()!;
    }

    private static long ReadInt64(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result))
            throw new NativeAdmissionException("invalid_discovery");
        return result;
    }

    private static ulong ReadU64(JsonElement value, string name)
    {
        var text = ReadString(value, name);
        if (text.Length is < 1 or > 20 || (text.Length > 1 && text[0] == '0') ||
            text.Any(character => character is < '0' or > '9') ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new NativeAdmissionException("invalid_discovery");
        return result;
    }

    private static bool IsIdentity(string value) => value.Length is >= 1 and <= 128 &&
        IsAsciiAlphanumeric(value[0]) && value.All(character => IsAsciiAlphanumeric(character) || character is '.' or '_' or ':' or '-');

    private static bool IsToken(string value) => value.Length is >= 1 and <= 63 &&
        (value[0] is (>= 'a' and <= 'z') or (>= '0' and <= '9')) &&
        value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static bool IsAsciiAlphanumeric(char value) => value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');
    private static void Invalid() => throw new NativeAdmissionException("invalid_discovery");

    private sealed record Authority(string Deployment, string Cell, string Publisher, ulong Generation);
    private sealed record Endpoint(string Url, HashSet<string> Capabilities);
    private sealed record Document(Authority Authority, ulong Revision, long PublishedAt, long ExpiresAt, int RefreshSeconds, IReadOnlyList<Endpoint> Endpoints);
    private sealed record Lease(Document Document, long StartedTimestamp, TimeSpan Lifetime);
    private sealed record Floor(Authority Authority, ulong Revision)
    {
        internal bool Accepts(Document document) => Authority.Deployment == document.Authority.Deployment &&
            Authority.Cell == document.Authority.Cell && document.Authority.Generation >= Authority.Generation && document.Revision >= Revision &&
            !(document.Authority.Generation == Authority.Generation && document.Revision == Revision && document.Authority.Publisher != Authority.Publisher);
    }

    /// <summary>Disposes an owned discovery transport once; a caller-owned HttpClient remains usable.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsClient) client.Dispose();
    }
}
