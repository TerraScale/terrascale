using System.Collections.Frozen;
using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

public sealed record ProjectAccess(string Actor, string ProjectId, string OrganizationId, string? DatabaseName, string? DatabaseId)
{
    public bool IsEndUser { get; init; }

    public IReadOnlySet<string> Roles { get; init; } = FrozenSet<string>.Empty;

    internal IReadOnlySet<string> Tables { get; init; } = FrozenSet<string>.Empty;

    private static readonly AsyncLocal<ProjectAccess?> Access = new();

    public static ProjectAccess? Current => Access.Value;

    internal static async Task RunAsync(ProjectAccess? access, Func<Task> next)
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

internal readonly union ProjectGrant(ProjectAccess, NotFound, Unavailable);
