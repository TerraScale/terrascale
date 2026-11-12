using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed record TsAuthImpersonationRequest(string Reason, string ClientId);

public sealed record TsAuthImpersonationTicket(string Id, string Ticket, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(TsAuthImpersonationTicket);
}

public sealed record TsAuthImpersonation(string Id, string UserId, string ActorId, string ActorEmail, string ClientId, string Reason, DateTimeOffset ExpiresAt);

public sealed record TsAuthImpersonations(IReadOnlyList<TsAuthImpersonation> Items);

public sealed partial class TsAuthProjectsClient
{
    public Task<TsAuthImpersonationTicket> StartImpersonationAsync(string accessToken, string projectId, string userId,
        TsAuthImpersonationRequest request, string proof, CancellationToken cancellationToken) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/users/" + Id(userId) + "/impersonations"), request,
            TsAuthImpersonationJson.Default.TsAuthImpersonationRequest, TsAuthImpersonationJson.Default.TsAuthImpersonationTicket,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, MaxRequestBytes, cancellationToken, TsAuthPlatformClient.Proof(proof));

    public Task<TsAuthImpersonations> ListImpersonationsAsync(string accessToken, string projectId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/impersonations", TsAuthImpersonationJson.Default.TsAuthImpersonations, HttpStatusCode.OK, cancellationToken);

    public Task EndImpersonationAsync(string accessToken, string projectId, string id, CancellationToken cancellationToken) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/impersonations/" + Id(id) + "/end"), null,
            Json.TsAuthPlatformName, TsAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TsAuthImpersonationRequest))]
[JsonSerializable(typeof(TsAuthImpersonationTicket))]
[JsonSerializable(typeof(TsAuthImpersonations))]
internal sealed partial class TsAuthImpersonationJson : JsonSerializerContext;
