namespace TerraScale.Platform.Features.FileStorage;

public readonly union ResolveFileScopeOutcome(
    FileScope,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public static class FileScopes
{
    private static readonly PlatformInvalidRequest BadQuery = new("only after (a canonical ULID) is accepted");
    private static readonly PlatformForbidden Restricted = new("an unrestricted project token is required", "forbidden");

    public static async ValueTask<ResolveFileScopeOutcome> ResolveAsync(
        PlatformAccess access,
        HttpContext context,
        string project,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (query.Keys.Any(static key => key != "after")
            || (query.TryGetValue("after", out var after) && (after.Count != 1 || !Ulid.TryParse(after[0], out _))))
        {
            return BadQuery;
        }

        var outcome = await access.ProjectAsync(context, project, scopes, cancellationToken);
        return outcome switch
        {
            AccessContext { Grant.DatabaseRestricted: true } => Restricted,
            AccessContext granted => new FileScope(granted.Organization, granted.Project, granted),
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }
}
