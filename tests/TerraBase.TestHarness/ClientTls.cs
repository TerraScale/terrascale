namespace TerraBase.Testing;

public sealed class ClientTls : IDisposable
{
    private bool _disposed;

    public void Apply(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public HttpClient CreateClient(Uri clientAddress, string? apiKey = null)
    {
        var client = new HttpClient(CreateHandler()) { BaseAddress = clientAddress, Timeout = Timeout.InfiniteTimeSpan };
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        }

        return client;
    }

    public SocketsHttpHandler CreateHandler()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new() { AllowAutoRedirect = false, UseCookies = false };
    }

    public void Dispose() => _disposed = true;
}

public static class ApiKeyIssuer
{
    public static Task<string> IssueAsync(this TerraBaseTestServer server, string organization, params string[] scopes) =>
        server.IssueNarrowedAsync(organization, project: null, database: null, scopes);

    public static Task<string> IssueAsync(this TerraBaseTestServer server, string organization, string[] scopes, CancellationToken cancellationToken) =>
        IssueCoreAsync(server, organization, project: null, database: null, scopes, cancellationToken);

    public static Task<string> IssueNarrowedAsync(
        this TerraBaseTestServer server, string organization, string? project, string? database, params string[] scopes) =>
        IssueCoreAsync(server, organization, project, database, scopes, CancellationToken.None);

    private static async Task<string> IssueCoreAsync(
        TerraBaseTestServer server, string organization, string? project, string? database, string[] scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var response = await server.Client.PostAsJsonAsync(
            new Uri("/admin/api-keys", UriKind.Relative),
            new { name = "test", scopes, organizationId = organization, projectId = project, databaseId = database }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IssuedKey>(cancellationToken);
        return body!.Secret;
    }

    private sealed record IssuedKey(string Secret);
}
