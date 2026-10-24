using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>An end user of the tenant.</summary>
public sealed record ProjectUserBody(string Id, string Email, DateTimeOffset CreatedAt, bool Disabled);

/// <summary>A page of end users.</summary>
public sealed record ProjectUserPageBody(IReadOnlyList<ProjectUserBody> Items, string? Next);

/// <summary>A live session of an end user.</summary>
public sealed record ProjectSessionBody(string Id, string UserId, string Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>A page of live sessions.</summary>
public sealed record ProjectSessionPageBody(IReadOnlyList<ProjectSessionBody> Items, string? Next);

/// <summary>
/// A project tenant management endpoint (<c>/v1/{project}/…</c>): the caller's terrascale sign-in token, then its
/// effective project role on every request.
/// </summary>
public abstract class ProjectTenantEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <summary>The tenant's management root.</summary>
    protected const string Root = "/v1/{project}";

    /// <summary>The tenant operations.</summary>
    protected TenantDirectory Tenants { get; } = tenants;

    /// <summary>The project id, once the caller holds at least <paramref name="minimum"/> on it.</summary>
    protected async Task<string> AccessAsync(UserRecord caller, string minimum, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var projectId = this.RouteId("project");
        await this.Tenants.CheckAccessAsync(caller.Id, projectId, minimum, cancellationToken);
        return projectId;
    }

    /// <summary>An end user.</summary>
    protected static ProjectUserBody Map(UserRecord user) => new(user.Id, user.Email, user.CreatedAt, user.Disabled);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProjectUserBody))]
[JsonSerializable(typeof(ProjectUserPageBody))]
[JsonSerializable(typeof(ProjectSessionPageBody))]
internal sealed partial class ProjectTenantJson : JsonSerializerContext;
