using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed record TsAuthAgentKey(string Kid, string Thumbprint, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
public sealed record TsAuthAgent(string Id, string ProjectId, string OwnerUserId, string Name, string Status,
    IReadOnlyList<string> AllowedScopes, bool AllowSelfSubject, IReadOnlyList<TsAuthAgentKey> Keys,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, bool ShareOwnerEmail = false);
public sealed record TsAuthAgentSharing(bool ShareOwnerEmail);
public sealed record TsAuthAgentPolicy(IReadOnlyList<string> AllowedScopes);

public sealed partial class TsAuthProjectsClient
{
    public Task<TsAuthPlatformPage<TsAuthAgent>> ListAgentsAsync(string accessToken, string projectId, string? owner, string? after, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/agents" + PageQuery("owner", owner, 25, after),
            TsAuthAgentsJson.Default.TsAuthPlatformPageTsAuthAgent, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthAgent> GetAgentAsync(string accessToken, string projectId, string agent, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/agents/" + Id(agent), TsAuthAgentsJson.Default.TsAuthAgent, HttpStatusCode.OK, cancellationToken);

    public Task RevokeAgentAsync(string accessToken, string projectId, string agent, CancellationToken cancellationToken) =>
        DeleteAsync(accessToken, projectId, "/agents/" + Id(agent), cancellationToken);

    public Task RevokeAgentKeyAsync(string accessToken, string projectId, string agent, string kid, CancellationToken cancellationToken) =>
        DeleteAsync(accessToken, projectId, "/agents/" + Id(agent) + "/keys/" + Id(kid), cancellationToken);

    public Task<TsAuthAgentPolicy> GetAgentPolicyAsync(string accessToken, string projectId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/agent-policy", TsAuthAgentsJson.Default.TsAuthAgentPolicy, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthAgentPolicy> UpdateAgentPolicyAsync(string accessToken, string projectId, TsAuthAgentPolicy policy, CancellationToken cancellationToken) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/agent-policy"), policy,
            TsAuthAgentsJson.Default.TsAuthAgentPolicy, TsAuthAgentsJson.Default.TsAuthAgentPolicy,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);
}

public sealed partial class TsAuthPlatformClient
{
    public Task<TsAuthPlatformPage<TsAuthAgent>> ListAccountAgentsAsync(string accessToken, string? after, CancellationToken cancellationToken) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, "/v1/terrascale/me/agents" + PageQuery(50, after),
            null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName, TsAuthAgentsJson.Default.TsAuthPlatformPageTsAuthAgent,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    public Task RevokeAccountAgentAsync(string accessToken, string agent, CancellationToken cancellationToken) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, "/v1/terrascale/me/agents/" + Id(agent),
            null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken);

    public Task<TsAuthAgent> ShareAccountAgentOwnerEmailAsync(string accessToken, string agent, bool share, CancellationToken cancellationToken) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Patch, "/v1/terrascale/me/agents/" + Id(agent), new TsAuthAgentSharing(share),
            TsAuthAgentsJson.Default.TsAuthAgentSharing, TsAuthAgentsJson.Default.TsAuthAgent,
            TsAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TsAuthAgent))]
[JsonSerializable(typeof(TsAuthPlatformPage<TsAuthAgent>))]
[JsonSerializable(typeof(TsAuthAgentPolicy))]
[JsonSerializable(typeof(TsAuthAgentSharing))]
internal sealed partial class TsAuthAgentsJson : JsonSerializerContext;
