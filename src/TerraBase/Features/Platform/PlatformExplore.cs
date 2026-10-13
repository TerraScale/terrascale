using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Platform;

/// <summary>A platform database as the explore API lists it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Id">Its fixed id.</param>
/// <param name="HomeRegion">The region it is managed in.</param>
/// <param name="ReplicaRegions">The other regions holding a copy.</param>
public sealed record PlatformDatabaseBody(string Name, string Id, string HomeRegion, IReadOnlyList<string> ReplicaRegions);

/// <summary>The platform databases.</summary>
/// <param name="Items">Every platform database.</param>
public sealed record PlatformDatabaseListBody(IReadOnlyList<PlatformDatabaseBody> Items);

/// <summary>
/// The read-only explore API over the platform databases, for platform admins in the dashboard: a ts-auth user access
/// token (not a project token) whose <c>platform_role</c> claim is <c>admin</c>, forwarded by the dashboard to the public
/// listener. Secret-looking fields are redacted before anything leaves; every read is logged with its actor.
/// </summary>
public static class PlatformExplore
{
    /// <summary>Authorization policy of the explore endpoints.</summary>
    public const string PolicyName = "platform-admin";

    /// <summary>The ts-auth claim that names a platform role.</summary>
    public const string RoleClaim = "platform_role";

    /// <summary>The platform role that may explore.</summary>
    public const string AdminRole = "admin";

    /// <summary>What a redacted value reads.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>Route of the platform databases.</summary>
    public const string Databases = "/v1/platform/databases";

    /// <summary>Route of one platform database's collections.</summary>
    public const string Collections = Databases + "/{database}/collections";

    /// <summary>Route of one collection's documents.</summary>
    public const string Documents = Collections + "/{collection}/documents";

    // Field names (lowercase, without '_' and '-') whose values never leave: credentials, sealed values, hashes, key
    // material. Over-redaction is the safe failure.
    private static readonly string[] SecretParts =
        ["secret", "password", "hash", "digest", "sealed", "box", "private", "token", "mac", "cipher", "nonce", "salt", "otp", "recovery", "credential", "masterkey"];

    private static readonly HashSet<string> SecretNames = ["d", "p", "q", "dp", "dq", "qi", "k", "key", "code", "codes", "pin"];

    /// <summary>Registers the platform-admin policy.</summary>
    public static IServiceCollection AddTerraBasePlatformExplore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthorizationBuilder().AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(UserAuthExtensions.TokenScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(RoleClaim, AdminRole));
        return services;
    }

    /// <summary>A copy of <paramref name="document"/> with every secret-looking field's value replaced by <see cref="Redacted"/>.</summary>
    public static JsonElement Redact(JsonElement document)
    {
        var node = JsonNode.Parse(document.GetRawText());
        RedactNode(node);
        return JsonSerializer.SerializeToElement(node, Hosting.TerraBaseJsonContext.Default.JsonNode);
    }

    /// <summary>Whether a field named <paramref name="name"/> is redacted.</summary>
    public static bool IsSecret(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return SecretNames.Contains(normalized) || SecretParts.Any(part => normalized.Contains(part, StringComparison.Ordinal));
    }

    /// <summary>The platform database named <paramref name="name"/>, or 404 for any other name.</summary>
    internal static async ValueTask<FindDatabaseResult> FindAsync(ITenantDirectory tenants, string? name, CancellationToken cancellationToken) =>
        name is not null && PlatformDatabases.Databases.TryGetValue(name, out var database)
            ? await tenants.FindDatabaseAsync(PlatformDatabases.OrganizationId, database.ProjectId, name, cancellationToken).ConfigureAwait(false)
            : new NotFound("the database does not exist", "unknown_database");

    /// <summary>The answer for a database <see cref="FindAsync"/> did not find.</summary>
    internal static IResult Refused(FindDatabaseResult result) => result switch
    {
        Unavailable error => ErrorResponse.From(error),
        _ => ErrorResponse.From(new NotFound("the database does not exist", "unknown_database")),
    };

    /// <summary>The audit trail: who read what, never the content.</summary>
    internal static void Audit(ILogger logger, HttpContext context, string database, string? collection, string? id) =>
        logger.ZLogInformation(
            $"Platform admin {context.User.UserId():@UserId} read {database:@Database}/{collection ?? "*":@Collection}/{id ?? "*":@DocumentId}");

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var name in value.Select(property => property.Key).ToList())
                {
                    if (IsSecret(name))
                    {
                        value[name] = Redacted;
                    }
                    else
                    {
                        RedactNode(value[name]);
                    }
                }

                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    RedactNode(item);
                }

                break;
        }
    }
}
