using TerraBase.Features.UserAuth;
using ZLogger;

namespace TerraBase.Features.Platform;

public sealed record PlatformDatabaseBody(string Name, string Id, string HomeRegion, IReadOnlyList<string> ReplicaRegions, bool AllRegions);

public sealed record PlatformDatabaseListBody(IReadOnlyList<PlatformDatabaseBody> Items);

public static class PlatformExplore
{
    public const string PolicyName = "platform-admin";

    public const string RoleClaim = "platform_role";

    public const string AdminRole = "admin";

    public const string Databases = "/v1/platform/databases";

    public static IServiceCollection AddTerraBasePlatformExplore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthorizationBuilder().AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(UserAuthExtensions.TokenScheme, UserAuthExtensions.OperatorScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(RoleClaim, AdminRole));
        return services;
    }

    internal static void Audit(ILogger logger, HttpContext context) =>
        logger.ZLogInformation($"Platform admin {context.User.UserId():@UserId} listed the platform databases");
}
