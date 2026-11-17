using System.Collections.Frozen;
using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

public sealed record ProjectAccess(string Actor, string ProjectId, string OrganizationId, string? DatabaseName, string? DatabaseId)
{
    public bool IsEndUser => Auth is not null;

    public IReadOnlySet<string> Roles { get; init; } = FrozenSet<string>.Empty;

    internal RulesAuth? Auth { get; init; }

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

internal readonly union ProjectAccessResult(ProjectAccess, NotFound, Unavailable);
