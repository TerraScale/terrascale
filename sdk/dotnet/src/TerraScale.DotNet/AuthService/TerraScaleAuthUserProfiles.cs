using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Creates a project user without sending email.</summary>
public sealed record TerraScaleAuthCreateUser(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password = null,
    [property: JsonPropertyName("first_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstName = null,
    [property: JsonPropertyName("last_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastName = null,
    [property: JsonPropertyName("image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl = null,
    [property: JsonPropertyName("external_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExternalId = null,
    [property: JsonPropertyName("public_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? PublicMetadata = null,
    [property: JsonPropertyName("private_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? PrivateMetadata = null,
    [property: JsonPropertyName("unsafe_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? UnsafeMetadata = null);

/// <summary>Merge patch. Omit with null; use a JSON null element to clear a scalar.</summary>
public sealed record TerraScaleAuthUpdateUser(
    [property: JsonPropertyName("first_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? FirstName = null,
    [property: JsonPropertyName("last_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? LastName = null,
    [property: JsonPropertyName("image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ImageUrl = null,
    [property: JsonPropertyName("external_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ExternalId = null,
    [property: JsonPropertyName("public_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? PublicMetadata = null,
    [property: JsonPropertyName("private_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? PrivateMetadata = null,
    [property: JsonPropertyName("unsafe_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? UnsafeMetadata = null);

/// <summary>Imported account. Hashes are never logged or returned.</summary>
public sealed record TerraScaleAuthImportUser(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("email_verified")] bool EmailVerified,
    [property: JsonPropertyName("password_hash"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PasswordHash = null,
    [property: JsonPropertyName("first_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstName = null,
    [property: JsonPropertyName("last_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastName = null,
    [property: JsonPropertyName("image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl = null,
    [property: JsonPropertyName("external_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExternalId = null,
    [property: JsonPropertyName("public_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? PublicMetadata = null,
    [property: JsonPropertyName("private_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? PrivateMetadata = null,
    [property: JsonPropertyName("unsafe_metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? UnsafeMetadata = null);

/// <summary>Imports one to 500 accounts.</summary>
public sealed record TerraScaleAuthImportUsers(
    [property: JsonPropertyName("users")] IReadOnlyList<TerraScaleAuthImportUser> Users);

/// <summary>One imported row and its zero-based input index.</summary>
public sealed record TerraScaleAuthImportedUser(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("user")] TerraScaleAuthProjectUser User);

/// <summary>One refused row.</summary>
public sealed record TerraScaleAuthUserImportError(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Import partial result; inspect Errors before treating the batch as complete.</summary>
public sealed record TerraScaleAuthImportUsersResult(
    [property: JsonPropertyName("items")] IReadOnlyList<TerraScaleAuthImportedUser> Items,
    [property: JsonPropertyName("errors")] IReadOnlyList<TerraScaleAuthUserImportError> Errors);

public sealed partial class TerraScaleAuthProjectsClient
{
    /// <summary>CreateUser with auth:write or the project admin role. Sent once.</summary>
    public Task<TerraScaleAuthProjectUser> CreateUserAsync(string accessToken, string projectId, TerraScaleAuthCreateUser request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/users"), request, Json.TerraScaleAuthCreateUser,
            Json.TerraScaleAuthProjectUser, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, 32 * 1024, cancellationToken);

    /// <summary>UpdateUser with auth:write or the project admin role. Sent once.</summary>
    public Task<TerraScaleAuthProjectUser> UpdateUserAsync(string accessToken, string projectId, string userId, TerraScaleAuthUpdateUser request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Patch, Route(projectId, "/users/" + Id(userId)), request, Json.TerraScaleAuthUpdateUser,
            Json.TerraScaleAuthProjectUser, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 32 * 1024, cancellationToken);

    /// <summary>ImportUsers with auth:write or the project admin role. Sent once.</summary>
    public Task<TerraScaleAuthImportUsersResult> ImportUsersAsync(string accessToken, string projectId, TerraScaleAuthImportUsers request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/users/import"), request, Json.TerraScaleAuthImportUsers,
            Json.TerraScaleAuthImportUsersResult, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024 * 1024, cancellationToken);

}
