using System.Collections.Frozen;
using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

public sealed record ProjectAccess(string Actor, string ProjectId, string OrganizationId, string? DatabaseName, string? DatabaseId)
{
    public bool IsEndUser { get; init; }

    public IReadOnlySet<string> Roles { get; init; } = FrozenSet<string>.Empty;

    internal TokenTables Tables { get; init; } = TokenTables.None;

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

internal sealed record TokenTables(IReadOnlySet<string>? Named)
{
    public static TokenTables None { get; } = new(FrozenSet<string>.Empty);

    public static TokenTables Every { get; } = new((IReadOnlySet<string>?)null);

    public bool Contains(string table) => Named is null || Named.Contains(table);
}

internal readonly union ProjectAccessResult(ProjectAccess, NotFound, Unavailable);
