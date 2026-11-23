namespace TerraScale.Platform.Features.FileStorage;

public static class FileScopes
{
    private static readonly Validation BadQuery = new("only after (a canonical ULID) is accepted");
    private static readonly Forbidden Restricted = new("an unrestricted project token is required", "forbidden");

    public static async ValueTask<Result<FileScope, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ResolveAsync(
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

        return await access.ProjectAsync(context, project, scopes, cancellationToken) switch
        {
            AccessContext { Grant.DatabaseRestricted: true } => Restricted,
            AccessContext granted => new FileScope(granted.Organization, granted.Project, granted),
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
