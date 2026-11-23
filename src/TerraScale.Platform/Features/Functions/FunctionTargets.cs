namespace TerraScale.Platform.Features.Functions;

public readonly union ResolveFunctionTargetOutcome(
    FunctionTarget,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public static class FunctionTargets
{
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
            DatabaseAccess granted => new FunctionTarget(granted.Access.Organization, granted.Access.Project, granted.Database, granted.DatabaseName),
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }
}
