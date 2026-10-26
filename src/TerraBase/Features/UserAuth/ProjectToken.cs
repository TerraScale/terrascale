using System.Net;
using TerraBase.Primitives;
namespace TerraBase.Features.UserAuth;

/// <summary>Project grants from an already validated ts-auth access token; never populated from request headers.</summary>
/// <param name="ProjectId">The token's project_id claim, if present.</param>
/// <param name="Database">Optional database name restriction.</param>
/// <param name="Scope">Space-separated granted scopes.</param>
/// <param name="ExpiresAt">The signed token's expiration, without clock skew.</param>
/// <param name="Allowlists">The IP allowlists the client address must fall in, every one; empty for none.</param>
/// <param name="Key">The API key the token was issued for, whose live status is checked on every request; null for other tokens.</param>
public sealed record ProjectToken(string? ProjectId, string? Database, string? Scope, DateTimeOffset ExpiresAt, IReadOnlyList<IPNetwork[]> Allowlists, KeyToken? Key = null)
{
    /// <summary>Whether the project and optional database restriction reach this route.</summary>
    public bool Reaches(string project, string database) =>
        EntityIds.IsValid(ProjectId) && project == ProjectId
        && (Database is null || Database == database);

    /// <summary>Whether the token grants one exact scope.</summary>
    public bool Grants(string scope) => Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal) == true;
}
