using TerraBase.Features.UserAuth;
using ZLogger;

namespace TerraBase.Features.Platform;

/// <summary>A platform database as the explore API lists it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Id">Its fixed id.</param>
/// <param name="HomeRegion">The region it is managed in.</param>
/// <param name="ReplicaRegions">The other regions holding a copy; empty when <paramref name="AllRegions"/>.</param>
/// <param name="AllRegions">Every serving region holds a copy, including regions that join later.</param>
public sealed record PlatformDatabaseBody(string Name, string Id, string HomeRegion, IReadOnlyList<string> ReplicaRegions, bool AllRegions);

/// <summary>The platform databases.</summary>
/// <param name="Items">Every platform database.</param>
public sealed record PlatformDatabaseListBody(IReadOnlyList<PlatformDatabaseBody> Items);

/// <summary>
/// The platform databases list for platform admins in the dashboard: a ts-auth user access token (not a project token)
/// whose <c>platform_role</c> claim is <c>admin</c>, forwarded by the dashboard to the public listener. Every read is
/// logged with its actor.
/// </summary>
public static class PlatformExplore
{
    /// <summary>Authorization policy of the platform endpoints.</summary>
    public const string PolicyName = "platform-admin";

    /// <summary>The ts-auth claim that names a platform role.</summary>
    public const string RoleClaim = "platform_role";

    /// <summary>The platform role that may list platform databases.</summary>
    public const string AdminRole = "admin";

    /// <summary>Route of the platform databases.</summary>
    public const string Databases = "/v1/platform/databases";

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

    /// <summary>The audit trail: who listed the platform databases.</summary>
    internal static void Audit(ILogger logger, HttpContext context) =>
        logger.ZLogInformation($"Platform admin {context.User.UserId():@UserId} listed the platform databases");
}
