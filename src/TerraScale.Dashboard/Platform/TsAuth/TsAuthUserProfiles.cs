using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed record TsAuthUserProfilePatch(string? FirstName, string? LastName, string? ImageUrl, string? ExternalId,
    JsonElement PublicMetadata, JsonElement PrivateMetadata, JsonElement UnsafeMetadata);

public sealed partial class TsAuthProjectsClient
{
    public Task<TsAuthProjectUser> GetUserAsync(string accessToken, string projectId, string userId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/users/" + Id(userId), Json.TsAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthProjectUser> UpdateUserAsync(string accessToken, string projectId, string userId, TsAuthUserProfilePatch patch,
        CancellationToken cancellationToken) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Patch, Route(projectId, "/users/" + Id(userId)), patch,
            UserProfileJson.Default.TsAuthUserProfilePatch, Json.TsAuthProjectUser, TsAuthCredentials.Bearer(accessToken),
            HttpStatusCode.OK, 32 * 1024, cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TsAuthUserProfilePatch))]
internal sealed partial class UserProfileJson : JsonSerializerContext;
