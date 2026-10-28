using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraScale.DotNet;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Demo;

// Test-only ts-auth project API keys, wire per I1 contracts/auth list/create/get/rotate/revoke_project_api_key. Only the global
// https://auth.terrascale.tech origin with the caller's original admitted sign-in bearer reaches it: exchanged project
// tokens, other hosts and regional fanout are refused. One instance per environment; nothing is static.
sealed partial class TestAuthApiKeys(Func<string, (string UserId, string Email)?> resolveSession, Func<string, string, string, string> role)
{
    public IAuthStepUpGateway? StepUp { get; set; }
    private int createdKeys, rotatedKeys;
    public int CreatedKeys => Volatile.Read(ref createdKeys);
    public int RotatedKeys => Volatile.Read(ref rotatedKeys);
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private static readonly string[] Allowed = ["databases:read", "databases:write", "documents:read", "documents:write"];
    private readonly TerraScaleIdGenerator ids = new();
    private readonly object gate = new();
    private readonly SortedDictionary<string, (Key Key, string Secret)> keys = new(StringComparer.Ordinal);
    private readonly List<(string Method, string Path, string UserId, string Token)> requests = [];
    private readonly Dictionary<string, string[]> projectCidrs = new(StringComparer.Ordinal);
    private TaskCompletionSource? listGate;
    private int pausedLists;
    public int PausedLists => Volatile.Read(ref pausedLists);
    public void PauseLists() { Interlocked.Exchange(ref pausedLists, 0); listGate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void ReleaseLists() { listGate?.TrySetResult(); listGate = null; }
    public (Key Key, string Secret)[] Issued() { lock (gate) return keys.Values.ToArray(); }
    public void ExpireAt(string keyId, DateTimeOffset expiresAt) { lock (gate) keys[keyId] = (keys[keyId].Key with { ExpiresAt = expiresAt }, keys[keyId].Secret); }
    // ts-auth's unauthenticated /readyz; a journey sets false to take ts-auth out of service.
    public bool Ready { get; set; } = true;

    public sealed record Key([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("organization_id")] string OrganizationId, [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("database")] string? Database, [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
        [property: JsonPropertyName("created_by")] string CreatedBy, [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("previous_secret_expires_at")] DateTimeOffset? PreviousSecretExpiresAt,
        [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string> AllowedCidrs,
        [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt = null);

    public object Observations()
    {
        lock (gate) return new
        {
            keys = keys.Values.Select(value => new { value.Key.Id, value.Key.Name, value.Key.ProjectId, value.Key.Database, value.Key.Scopes, secret = value.Secret }).ToArray(),
            requests = requests.Select(value => new
            {
                method = value.Method,
                path = value.Path,
                userId = value.UserId,
                bearerKind = "original-session",
                authorization = "Bearer " + value.Token
            }).ToArray()
        };
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Scheme != "https" || uri.Host != "auth.terrascale.tech" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new HttpRequestException("Project API keys are issued only by the global ts-auth origin.");
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/readyz")
            return Ready ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") } : Error(HttpStatusCode.ServiceUnavailable, "not_ready");
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } token } || resolveSession(token) is not { } actor)
            return Error(HttpStatusCode.Unauthorized, "invalid_token");
        var match = Route().Match(uri.AbsolutePath);
        if (!match.Success) return Error(HttpStatusCode.NotFound, "not_found");
        string organization = match.Groups["organization"].Value, project = match.Groups["project"].Value, keyId = match.Groups["key"].Value;
        var rotate = match.Groups["rotate"].Success;
        var cidrsRoute = match.Groups["cidrs"].Success;
        lock (gate) requests.Add((request.Method.Method, uri.AbsolutePath, actor.UserId, token));
        async Task<bool> ConfirmAsync(string purpose)
        {
            try
            {
                if (StepUp is null || !request.Headers.TryGetValues("X-TerraScale-Step-Up-Proof", out var proofs)) return false;
                await StepUp.ConsumeAsync(new(actor.UserId, "Synthetic", actor.Email, token, null, DateTimeOffset.UtcNow.AddHours(1)),
                    proofs.Single(), purpose, "ts-auth", null, cancellationToken);
                return true;
            }
            catch (AccountAccessException) { return false; }
        }
        var current = role(actor.UserId, organization, project);
        if (current.Length == 0) return Error(HttpStatusCode.NotFound, "not_found");
        var manage = current is "owner" or "admin";
        if (!match.Groups["keys"].Success)
        {
            // ts-auth's project body, reduced to what the dashboard reads; PUT .../allowed-cidrs needs project admin.
            if (request.Method == HttpMethod.Put && cidrsRoute)
            {
                if (!manage) return Error(HttpStatusCode.Forbidden, "access_denied");
                if (await CidrsAsync(request, cancellationToken) is not { } replaced) return Error(HttpStatusCode.BadRequest, "invalid_request", "allowed_cidrs must hold up to 32 valid IPs or CIDR ranges");
                lock (gate) projectCidrs[project] = replaced;
            }
            else if (request.Method != HttpMethod.Get || cidrsRoute) return Error(HttpStatusCode.MethodNotAllowed, "invalid_request");
            lock (gate) return Json(new { id = project, organization_id = organization, allowed_cidrs = projectCidrs.GetValueOrDefault(project) ?? [] });
        }
        if (cidrsRoute)
        {
            if (request.Method != HttpMethod.Put) return Error(HttpStatusCode.MethodNotAllowed, "invalid_request");
            if (!manage) return Error(HttpStatusCode.Forbidden, "access_denied");
            if (await CidrsAsync(request, cancellationToken) is not { } replaced) return Error(HttpStatusCode.BadRequest, "invalid_request", "allowed_cidrs must hold up to 32 valid IPs or CIDR ranges");
            lock (gate)
            {
                if (!keys.TryGetValue(keyId, out var stored) || stored.Key.OrganizationId != organization || stored.Key.ProjectId != project)
                    return Error(HttpStatusCode.NotFound, "api_key_not_found");
                keys[keyId] = (stored.Key with { AllowedCidrs = replaced }, stored.Secret);
                return Json(keys[keyId].Key);
            }
        }
        if (keyId.Length == 0 && request.Method == HttpMethod.Get)
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            if (query.Keys.Except(["limit", "after"]).Any()) return Error(HttpStatusCode.BadRequest, "invalid_request");
            var limit = 20;
            if (query.TryGetValue("limit", out var text) && (!int.TryParse(text, out limit) || limit is < 1 or > 100)) return Error(HttpStatusCode.BadRequest, "invalid_request");
            var after = query.GetValueOrDefault("after").ToString();
            if (listGate is { } paused) { Interlocked.Increment(ref pausedLists); await paused.Task.WaitAsync(cancellationToken); }
            lock (gate)
            {
                var scoped = keys.Values.Where(value => value.Key.OrganizationId == organization && value.Key.ProjectId == project).Select(value => value.Key).ToArray();
                if (after.Length > 0 && !scoped.Any(key => key.Id == after)) return Error(HttpStatusCode.BadRequest, "invalid_cursor");
                var page = scoped.Where(key => string.CompareOrdinal(key.Id, after) > 0).Take(limit + 1).ToArray();
                return Json(new { items = page.Take(limit), next = page.Length > limit ? page[limit - 1].Id : null });
            }
        }
        if (keyId.Length == 0 && request.Method == HttpMethod.Post)
        {
            if (!await ConfirmAsync("create_api_key")) return Error(HttpStatusCode.Forbidden, "step_up_required");
            Interlocked.Increment(ref createdKeys);
            if (!manage) return Error(HttpStatusCode.Forbidden, "access_denied");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(field => field.Name is not ("name" or "scopes" or "database" or "expires_at" or "allowed_cidrs")) ||
                !root.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 and <= 128 } keyName ||
                !root.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array || scopes.GetArrayLength() == 0)
                return Error(HttpStatusCode.BadRequest, "invalid_request");
            var granted = scopes.EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
            if (granted.Any(scope => !Allowed.Contains(scope)) || granted.Distinct(StringComparer.Ordinal).Count() != granted.Length) return Error(HttpStatusCode.BadRequest, "invalid_scope");
            var database = root.TryGetProperty("database", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
            DateTimeOffset? expires = root.TryGetProperty("expires_at", out var expiry) && expiry.ValueKind == JsonValueKind.String ? expiry.GetDateTimeOffset() : null;
            if (Canonical(root) is not { } allowed) return Error(HttpStatusCode.BadRequest, "invalid_request", "allowed_cidrs must hold up to 32 valid IPs or CIDR ranges");
            var key = new Key(ids.NewId(), keyName, organization, project, database, granted, actor.UserId, DateTimeOffset.UtcNow, expires, null, allowed);
            var secret = Secret(key.Id);
            lock (gate) keys[key.Id] = (key, secret);
            return Json(new { api_key = key, key = secret }, HttpStatusCode.Created);
        }
        if (rotate)
        {
            if (!await ConfirmAsync("rotate_api_key")) return Error(HttpStatusCode.Forbidden, "step_up_required");
            Interlocked.Increment(ref rotatedKeys);
            if (request.Method != HttpMethod.Post) return Error(HttpStatusCode.MethodNotAllowed, "invalid_request");
            if (!manage) return Error(HttpStatusCode.Forbidden, "access_denied");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(field => field.Name != "previous_secret_ttl")) return Error(HttpStatusCode.BadRequest, "invalid_request");
            var ttl = 0;
            if (root.TryGetProperty("previous_secret_ttl", out var grace) && grace.ValueKind != JsonValueKind.Null && (!grace.TryGetInt32(out ttl) || ttl is < 0 or > 86400))
                return Error(HttpStatusCode.BadRequest, "invalid_request");
            lock (gate)
            {
                if (!keys.TryGetValue(keyId, out var stored) || stored.Key.OrganizationId != organization || stored.Key.ProjectId != project)
                    return Error(HttpStatusCode.NotFound, "api_key_not_found");
                if (stored.Key.PreviousSecretExpiresAt > DateTimeOffset.UtcNow) return Error(HttpStatusCode.Conflict, "rotation_pending");
                var key = stored.Key with { PreviousSecretExpiresAt = ttl > 0 ? DateTimeOffset.UtcNow.AddSeconds(ttl) : null };
                var secret = Secret(key.Id);
                keys[key.Id] = (key, secret);
                return Json(new { api_key = key, key = secret });
            }
        }
        lock (gate)
        {
            if (!keys.TryGetValue(keyId, out var stored) || stored.Key.OrganizationId != organization || stored.Key.ProjectId != project)
                return Error(HttpStatusCode.NotFound, "api_key_not_found");
            if (request.Method == HttpMethod.Get) return Json(stored.Key);
            if (request.Method != HttpMethod.Delete) return Error(HttpStatusCode.MethodNotAllowed, "invalid_request");
            if (!manage) return Error(HttpStatusCode.Forbidden, "access_denied");
            keys.Remove(keyId);
            return new(HttpStatusCode.NoContent);
        }
    }

    // Like ts-auth: bare IPs become /32 or /128, an absent or empty list allows any IP.
    private static string[]? Canonical(JsonElement root)
    {
        if (!root.TryGetProperty("allowed_cidrs", out var list) || list.ValueKind == JsonValueKind.Null) return [];
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 32) return null;
        var canonical = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
            if (IPNetwork.TryParse(text, out var network)) canonical.Add(network.ToString());
            else if (IPAddress.TryParse(text, out var address)) canonical.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128).ToString());
            else return null;
        }
        return canonical.Distinct(StringComparer.Ordinal).ToArray();
    }
    private static async Task<string[]?> CidrsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.EnumerateObject().All(field => field.Name == "allowed_cidrs") &&
            body.RootElement.TryGetProperty("allowed_cidrs", out _) ? Canonical(body.RootElement) : null;
    }

    [GeneratedRegex("^/v1/terrascale/organizations/(?<organization>[0-9A-HJKMNP-TV-Z]{26})/projects/(?<project>[0-9A-HJKMNP-TV-Z]{26})(?:(?<keys>/api-keys(?:/(?<key>[0-9A-HJKMNP-TV-Z]{26})(?:(?<rotate>/rotate)|(?<cidrs>/allowed-cidrs))?)?)|(?<cidrs>/allowed-cidrs))?$")]
    private static partial Regex Route();
    private static string Secret(string keyId) => keyId + "_" + string.Concat(Enumerable.Range(0, 52).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), new MediaTypeHeaderValue("application/json")) };
    // ts-auth management errors use the nested envelope (TerraScaleAuthErrors).
    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message = "Test ts-auth rejected the request") => Json(new { error = new { code, message } }, status);
}
