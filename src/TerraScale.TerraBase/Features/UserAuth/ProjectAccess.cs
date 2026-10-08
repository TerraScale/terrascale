namespace TerraBase.Features.UserAuth;

/// <summary>A verified project token's access to the existing storage namespace, scoped to one request.</summary>
/// <param name="Actor">The signed token's subject.</param>
/// <param name="ProjectId">The signed token's project.</param>
/// <param name="OrganizationId">The catalog's storage namespace; never read from a token.</param>
/// <param name="DatabaseName">A database restriction from the signed token, when present.</param>
/// <param name="DatabaseId">The catalog id of that restricted database, when present.</param>
public sealed record ProjectAccess(string Actor, string ProjectId, string OrganizationId, string? DatabaseName, string? DatabaseId)
{
    private static readonly AsyncLocal<ProjectAccess?> Access = new();

    /// <summary>The project grant of the current async request, after authorization.</summary>
    public static ProjectAccess? Current => Access.Value;

    internal static async Task RunAsync(ProjectAccess access, Func<Task> next)
    {
        var previous = Access.Value;
        Access.Value = access;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            Access.Value = previous;
        }
    }
}
