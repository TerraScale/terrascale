using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseManagementClientOptions
{
    public Uri? BaseAddress { get; init; }

    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    public string? Region { get; init; }

    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    public override string ToString() => $"TerraBase management client options for {BaseAddress}";
}

public sealed class TerraBaseManagementClient
{
    private const string Root = "/v1/management";
    private const string UserHeader = "X-TerraBase-User-Id";
    private const string OrganizationHeader = "X-TerraBase-Organization-Id";
    private const int MaxUserIdLength = 256;
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    private readonly TerraBaseConnection connection;

    public TerraBaseManagementClient(HttpClient httpClient, TerraBaseManagementClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, null, nameof(options.AccessTokenProvider), TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes,
            options.AccessTokenProvider, anonymousAllowed: true, region: options.Region);
    }

    internal TerraBaseManagementClient(TerraBaseConnection connection) => this.connection = connection;


    public Task<TerraBaseOrganization> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Root + "/organizations", userId, null, new RequestBody { Name = name }, Json.TerraBaseOrganization, cancellationToken);

    public Task<TerraBasePage<TerraBaseOrganization>> ListOrganizationsAsync(string userId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Root + "/organizations" + Page(after, limit), userId, null, null, Json.TerraBasePageTerraBaseOrganization, cancellationToken);

    public Task<TerraBaseOrganization> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId), userId, organizationId, null, Json.TerraBaseOrganization, cancellationToken);

    public Task<TerraBaseOrganization> SetPlanAsync(string userId, string organizationId, TerraBasePlan plan, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, Organization(organizationId) + "/plan", userId, organizationId, new RequestBody { Plan = plan }, Json.TerraBaseOrganization, cancellationToken);

    public Task DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Organization(organizationId), userId, organizationId, null, cancellationToken);


    public Task<TerraBaseProject> CreateProjectAsync(string userId, string organizationId, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Organization(organizationId) + "/projects", userId, organizationId, new RequestBody { Name = name }, Json.TerraBaseProject, cancellationToken);

    public Task<TerraBasePage<TerraBaseProject>> ListProjectsAsync(
        string userId, string organizationId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId) + "/projects" + Page(after, limit), userId, organizationId, null, Json.TerraBasePageTerraBaseProject, cancellationToken);

    public Task<TerraBaseProject> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Project(organizationId, projectId), userId, organizationId, null, Json.TerraBaseProject, cancellationToken);

    public Task DeleteProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Project(organizationId, projectId), userId, organizationId, null, cancellationToken);

    public Task<TerraBaseProjectClosure> CloseProjectAsync(string projectId, string authorityOrganizationId, string closureId, CancellationToken cancellationToken = default) =>
        SendProjectClosureAsync(projectId, authorityOrganizationId, closureId, 1, cancellationToken);

    public Task<TerraBaseProjectClosure> EraseProjectAsync(string projectId, string authorityOrganizationId, string closureId, CancellationToken cancellationToken = default) =>
        SendProjectClosureAsync(projectId, authorityOrganizationId, closureId, 2, cancellationToken);

    private async Task<TerraBaseProjectClosure> SendProjectClosureAsync(string projectId, string authorityOrganizationId, string closureId, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorityOrganizationId);
        ArgumentException.ThrowIfNullOrEmpty(closureId);
        var body = TerraBaseConnection.Json(new RequestBody { Version = version, ClosureId = closureId, AuthorityOrganizationId = authorityOrganizationId }, Json.RequestBody);
        var (closure, status) = await connection.SendAsync(HttpMethod.Put, "/admin/projects/" + TerraScalePaths.Segment(projectId, nameof(projectId)) + "/closure",
            body, Json.TerraBaseProjectClosure, null, cancellationToken).ConfigureAwait(false);
        return closure.ClosureId == closureId && (status, closure.Status) is (System.Net.HttpStatusCode.OK, "accepted") or (System.Net.HttpStatusCode.Accepted, "pending")
            ? closure
            : throw new TerraBaseException(status, "TerraBase answered a project closure with a status or closure id that does not match the request.", "invalid_response", null);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, string userId, string? organizationId, RequestBody? body, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var headers = Identity(userId, organizationId);
        var content = body is null ? null : TerraBaseConnection.Json(body, Json.RequestBody);
        return (await connection.SendAsync(method, path, content, typeInfo, headers, cancellationToken).ConfigureAwait(false)).Value;
    }

    private Task SendAsync(HttpMethod method, string path, string userId, string? organizationId, RequestBody? body, CancellationToken cancellationToken)
    {
        var headers = Identity(userId, organizationId);
        return connection.SendAsync(method, path, body is null ? null : TerraBaseConnection.Json(body, Json.RequestBody), headers, cancellationToken);
    }

    private static Action<HttpRequestHeaders> Identity(string userId, string? organizationId)
    {
        if (string.IsNullOrEmpty(userId) || userId.Length > MaxUserIdLength || userId.Any(char.IsControl))
        {
            throw new ArgumentException("userId must be a ts-auth user id of 1 to 256 characters without control characters.", nameof(userId));
        }

        if (organizationId is not null && (organizationId.Length == 0 || organizationId.Any(char.IsControl)))
        {
            throw new ArgumentException("organizationId must be non-empty and contain no control characters.", nameof(organizationId));
        }

        return headers =>
        {
            headers.TryAddWithoutValidation(UserHeader, userId);
            if (organizationId is not null)
            {
                headers.TryAddWithoutValidation(OrganizationHeader, organizationId);
            }
        };
    }

    private static string Page(string? after, int? limit) =>
        TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit)));

    private static string Organization(string organizationId) => $"{Root}/organizations/{TerraScalePaths.Segment(organizationId, nameof(organizationId))}";

    private static string Project(string organizationId, string projectId) =>
        $"{Organization(organizationId)}/projects/{TerraScalePaths.Segment(projectId, nameof(projectId))}";
}
