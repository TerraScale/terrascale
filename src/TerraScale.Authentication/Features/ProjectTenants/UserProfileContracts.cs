using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record UserProfilePatch
{
    public JsonElement FirstName { get; init; }
    public JsonElement LastName { get; init; }
    public JsonElement ImageUrl { get; init; }
    public JsonElement ExternalId { get; init; }
    public JsonElement PublicMetadata { get; init; }
    public JsonElement PrivateMetadata { get; init; }
    public JsonElement UnsafeMetadata { get; init; }
}

public sealed record CreateProjectUserRequest(string Email, string? Password = null, string? FirstName = null, string? LastName = null,
    string? ImageUrl = null, string? ExternalId = null)
{
    public JsonElement PublicMetadata { get; init; }
    public JsonElement PrivateMetadata { get; init; }
    public JsonElement UnsafeMetadata { get; init; }
}

public sealed record ImportProjectUserRequest(string Email, string? PasswordHash = null, bool EmailVerified = false,
    string? FirstName = null, string? LastName = null, string? ImageUrl = null, string? ExternalId = null)
{
    public JsonElement PublicMetadata { get; init; }
    public JsonElement PrivateMetadata { get; init; }
    public JsonElement UnsafeMetadata { get; init; }
}

public sealed record ImportProjectUsersRequest(IReadOnlyList<ImportProjectUserRequest> Users);
public sealed record UserImportError(int Index, string Code, string Message);
public sealed record ImportedProjectUser(int Index, ProjectUserBody User);
public sealed record ImportProjectUsersBody(IReadOnlyList<ImportedProjectUser> Items, IReadOnlyList<UserImportError> Errors);

public sealed record SelfProfilePatch
{
    public JsonElement FirstName { get; init; }
    public JsonElement LastName { get; init; }
    public JsonElement UnsafeMetadata { get; init; }
}
public sealed record SelfProfileBody(string Id, string Email, string? FirstName, string? LastName, string? ImageUrl,
    JsonElement PublicMetadata, JsonElement UnsafeMetadata)
{
    public static SelfProfileBody From(UserRecord user) => new(user.Id, user.Email, user.FirstName, user.LastName, user.ImageUrl,
        JsonElement.Parse(user.PublicMetadata), JsonElement.Parse(user.UnsafeMetadata));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CreateProjectUserRequest))]
[JsonSerializable(typeof(UserProfilePatch))]
[JsonSerializable(typeof(ImportProjectUsersRequest))]
[JsonSerializable(typeof(ImportProjectUsersBody))]
[JsonSerializable(typeof(SelfProfilePatch))]
[JsonSerializable(typeof(SelfProfileBody))]
internal sealed partial class UserProfileJson : JsonSerializerContext;
