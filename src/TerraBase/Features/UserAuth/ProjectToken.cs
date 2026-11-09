using System.Net;
using TerraBase.Primitives;
namespace TerraBase.Features.UserAuth;

public sealed record ProjectToken(string? ProjectId, string? Database, string? Scope, DateTimeOffset ExpiresAt, IReadOnlyList<IPNetwork[]> Allowlists, KeyToken? Key = null)
{
    public bool Reaches(string project, string database) =>
        EntityIds.IsValid(ProjectId) && project == ProjectId
        && (Database is null || Database == database);

    public bool Grants(string scope) => Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal) == true;
}
