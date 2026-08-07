using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>
/// Client for project team-member access endpoints.
/// </summary>
public sealed class TerraScaleProjectAccessClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleProjectAccessClient(TerraScaleClient client) => this.client = client;

    /// <summary>
    /// Lists team members for a project.
    /// </summary>
    public Task<TerraScaleProjectTeamMemberPage> ListTeamMembersAsync(
        string projectId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        TerraScalePagination.Validate(skip, take);
        return client.SendJsonAsync(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectTeamMembersList(projectId, skip, take),
            TypeInfo<TerraScaleProjectTeamMemberPage>(),
            cancellationToken);
    }

    /// <summary>
    /// Adds a user to a project's team.
    /// </summary>
    public Task<TerraScaleProjectTeamMember> AddTeamMemberAsync(
        string projectId,
        TerraScaleAddProjectTeamMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Role);
        return client.SendNonRetryableJsonAsync(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectTeamMembersCreate(projectId),
            request,
            TypeInfo<TerraScaleAddProjectTeamMemberRequest>(),
            TypeInfo<TerraScaleProjectTeamMember>(),
            cancellationToken);
    }

    /// <summary>
    /// Updates a project team member's role.
    /// </summary>
    public Task<TerraScaleProjectTeamMember> UpdateTeamMemberAsync(
        string projectId,
        string userId,
        TerraScaleUpdateProjectTeamMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Role);
        return client.SendNonRetryableJsonAsync(
            HttpMethod.Patch,
            TerraScaleApiRoutes.ProjectTeamMemberUpdate(projectId, userId),
            request,
            TypeInfo<TerraScaleUpdateProjectTeamMemberRequest>(),
            TypeInfo<TerraScaleProjectTeamMember>(),
            cancellationToken);
    }

    /// <summary>
    /// Removes a user from a project's team.
    /// </summary>
    public Task RemoveTeamMemberAsync(
        string projectId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return client.SendNonRetryableNoContentAsync(
            HttpMethod.Delete,
            TerraScaleApiRoutes.ProjectTeamMemberDelete(projectId, userId),
            cancellationToken);
    }

    private void ValidateProjectId(string projectId) => client.ValidateConfiguredProjectId(projectId);

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>)TerraScaleProjectAccessJsonContext.Default.GetTypeInfo(typeof(T))!;
}

/// <summary>
/// A page of project team members.
/// </summary>
public sealed record TerraScaleProjectTeamMemberPage(
    IReadOnlyList<TerraScaleProjectTeamMember> Items,
    int TotalCount,
    string? NextCursor);

/// <summary>
/// A member of a project's team.
/// </summary>
public sealed record TerraScaleProjectTeamMember(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    DateTimeOffset? JoinedAt);

/// <summary>
/// Request to add a user to a project's team.
/// </summary>
public sealed record TerraScaleAddProjectTeamMemberRequest(string UserId, string Role);

/// <summary>
/// Request to update a project team member's role.
/// </summary>
public sealed record TerraScaleUpdateProjectTeamMemberRequest(string Role);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TerraScaleProjectTeamMemberPage))]
[JsonSerializable(typeof(TerraScaleProjectTeamMember))]
[JsonSerializable(typeof(TerraScaleAddProjectTeamMemberRequest))]
[JsonSerializable(typeof(TerraScaleUpdateProjectTeamMemberRequest))]
internal partial class TerraScaleProjectAccessJsonContext : JsonSerializerContext;
