namespace TerraScale.Platform.Features.Functions;

public readonly union ResolveFunctionTargetOutcome(
    FunctionTarget,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed record FunctionBodyLimit(long Bytes, IReadOnlyList<string> Scopes);

public static class FunctionTargets
{
    private static readonly PlatformForbidden DeveloperOnly =
        new("functions accept only a project API key or developer token; end-user and guest tokens are refused", "developer_credential_required");

    public static async ValueTask<ResolveFunctionTargetOutcome> ResolveAsync(
        PlatformAccess access,
        HttpContext context,
        string project,
        string database,
        string? function,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        if (function is not null && FunctionNames.Check(function) is { } invalid)
        {
            return invalid;
        }

        var outcome = await access.DatabaseAsync(context, project, database, scopes, cancellationToken);
        return outcome switch
        {
            DatabaseAccess { Access.Grant.EndUser: true } => DeveloperOnly,
            DatabaseAccess granted => new FunctionTarget(granted.Access.Organization, granted.Access.Project, granted.Database, granted.DatabaseName),
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }
}
