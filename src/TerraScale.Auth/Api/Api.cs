namespace TerraScale.Auth.Api;

/// <summary>Shared endpoint helpers: the ts-auth error envelope.</summary>
public static class ApiResults
{
    /// <summary>The uniform error body: <c>{ "error": { code, message, ... } }</c>.</summary>
    public static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    public static IResult BadRequest(string code, string message) => Error(400, code, message);
    public static IResult Unauthorized(string code = "unauthorized", string message = "Missing or invalid credentials.") => Error(401, code, message);
    public static IResult Forbidden(string message = "Not allowed.") => Error(403, "forbidden", message);
    public static IResult NotFound(string code = "not_found", string message = "Not found.") => Error(404, code, message);
    public static IResult Conflict(string code, string message) => Error(409, code, message);
}

/// <summary>The instance context resolved for a request, plus who resolved it.</summary>
public sealed record InstanceContext(Store.Instance Instance, string? ApiKeyId = null);

/// <summary>Resolves the ts-auth instance for management API requests from the bearer API key.</summary>
public sealed class InstanceResolver(Store.AuthStore store)
{
    private const string ContextItemKey = "ts-auth:instance-context";

    /// <summary>Maps a request's Authorization header to an instance; null when not authenticated.
    /// The rate-limit middleware resolves sk_ requests before any handler runs; the
    /// result is cached per request so handlers reuse it instead of re-querying the store.</summary>
    public async Task<InstanceContext?> ResolveAsync(HttpRequest request, CancellationToken ct = default)
    {
        if (request.HttpContext.Items[ContextItemKey] is InstanceContext cached)
            return cached;

        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var secret = authorization["Bearer ".Length..].Trim();
        if (!secret.StartsWith("sk_"))
            return null;

        var apiKey = await store.FindApiKeyAsync(Crypto.SecureTokens.Sha256Hex(secret), ct);
        if (apiKey is null || apiKey.Revoked)
            return null;

        var instance = await store.GetInstanceAsync(apiKey.InstanceId, ct);
        if (instance is null)
            return null;

        var context = new InstanceContext(instance, apiKey.Id);
        request.HttpContext.Items[ContextItemKey] = context;
        return context;
    }
}
