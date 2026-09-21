namespace TerraScale.Auth.Templates;

using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

internal static class JwtTemplateSupport
{
    // ---- Shared helpers (also used by OrganizationDomainSupport) -----------------------------

    /// <summary>Resolves the sk_ key (401 on a bad key), checks the slug matches the
    /// key's instance (401 on mismatch — a foreign-instance key must look exactly like
    /// an invalid one), then runs the handler with the instance context.</summary>
    internal static async Task<IResult> WithScopeAsync(
        InstanceResolver resolver, HttpRequest request, string slug,
        Func<InstanceContext, Task<IResult>> handler, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();
        return await handler(context);
    }

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = Now(),
        }, ct);

    internal static bool IsValidName(string? name)
    {
        var value = name?.Trim().ToLowerInvariant();
        return value is { Length: >= 1 and <= 48 }
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    internal static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    internal static async Task<JwtTemplate?> FindByNameAsync(AuthStore store, string instanceId, string name, CancellationToken ct) =>
        (await store.ListJwtTemplatesAsync(instanceId, ct)).FirstOrDefault(t => t.Name == name);

    internal static object Render(JwtTemplate template) =>
        new { id = template.Id, name = template.Name, claims = template.Claims, lifetime_seconds = template.LifetimeSeconds, created_at = template.CreatedAt };
}
