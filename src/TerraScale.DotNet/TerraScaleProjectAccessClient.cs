using System.Diagnostics.CodeAnalysis;
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
        TerraScaleProjectAccessJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"JSON metadata for {typeof(T)} is not available in the project-access context.");
}

/// <summary>
/// A page of project team members.
/// </summary>
public sealed record TerraScaleProjectTeamMemberPage : IJsonOnDeserialized
{
    /// <summary>
    /// Creates a page for required-property object initialization.
    /// </summary>
    public TerraScaleProjectTeamMemberPage()
    {
    }

    /// <summary>
    /// Creates a page of project team members.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    public TerraScaleProjectTeamMemberPage(
        IReadOnlyList<TerraScaleProjectTeamMember> items,
        int totalCount,
        string? nextCursor)
    {
        Items = items;
        TotalCount = totalCount;
        NextCursor = nextCursor;
    }

    /// <summary>
    /// Team members in this page.
    /// </summary>
    [JsonRequired]
    public required IReadOnlyList<TerraScaleProjectTeamMember> Items { get; init; }

    /// <summary>
    /// Total number of team members available.
    /// </summary>
    [JsonRequired]
    public required int TotalCount { get; init; }

    /// <summary>
    /// Cursor for the next page, when available.
    /// </summary>
    [JsonRequired]
    public required string? NextCursor { get; init; }

    /// <summary>
    /// Deconstructs this page into its positional record fields.
    /// </summary>
    public void Deconstruct(
        out IReadOnlyList<TerraScaleProjectTeamMember> items,
        out int totalCount,
        out string? nextCursor)
    {
        items = Items;
        totalCount = TotalCount;
        nextCursor = NextCursor;
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Items is null)
        {
            throw new JsonException("TerraScale team-member page response requires a non-null items field.");
        }
    }
}

/// <summary>
/// A member of a project's team.
/// </summary>
public sealed record TerraScaleProjectTeamMember : IJsonOnDeserialized
{
    /// <summary>
    /// Creates a member for required-property object initialization.
    /// </summary>
    public TerraScaleProjectTeamMember()
    {
    }

    /// <summary>
    /// Creates a project team member.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    public TerraScaleProjectTeamMember(
        string userId,
        string email,
        string displayName,
        string role,
        DateTimeOffset? joinedAt)
    {
        UserId = userId;
        Email = email;
        DisplayName = displayName;
        Role = role;
        JoinedAt = joinedAt;
    }

    /// <summary>
    /// Team member user identifier.
    /// </summary>
    [JsonRequired]
    public required string UserId { get; init; }

    /// <summary>
    /// Team member email address.
    /// </summary>
    [JsonRequired]
    public required string Email { get; init; }

    /// <summary>
    /// Team member display name.
    /// </summary>
    [JsonRequired]
    public required string DisplayName { get; init; }

    /// <summary>
    /// Team member role supplied by the public API.
    /// </summary>
    [JsonRequired]
    public required string Role { get; init; }

    /// <summary>
    /// Time the user joined the project, or null when the API explicitly provides null.
    /// </summary>
    [JsonRequired]
    public required DateTimeOffset? JoinedAt { get; init; }

    /// <summary>
    /// Deconstructs this member into its positional record fields.
    /// </summary>
    public void Deconstruct(
        out string userId,
        out string email,
        out string displayName,
        out string role,
        out DateTimeOffset? joinedAt)
    {
        userId = UserId;
        email = Email;
        displayName = DisplayName;
        role = Role;
        joinedAt = JoinedAt;
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (UserId is null || Email is null || DisplayName is null || Role is null)
        {
            throw new JsonException("TerraScale team-member response requires non-null userId, email, displayName, and role fields.");
        }
    }
}

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
