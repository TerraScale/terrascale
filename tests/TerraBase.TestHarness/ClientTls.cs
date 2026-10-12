namespace TerraBase.Testing;

/// <summary>
/// Clients of a test server's public listener: plain HTTP, as Fly Proxy forwards it once TLS ends at Fly's edge. It owns
/// nothing but its own lifetime: use it only before it is disposed.
/// </summary>
public sealed class ClientTls : IDisposable
{
    private bool _disposed;

    /// <summary>Nothing to set: every server listens publicly.</summary>
    public void Apply(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>A client of the public listener that, when <paramref name="apiKey"/> is set, sends it as a bearer token.</summary>
    public HttpClient CreateClient(Uri clientAddress, string? apiKey = null)
    {
        // The test timeout bounds a request: serialized durable commits can take longer than any fixed limit on a busy runner.
        var client = new HttpClient(CreateHandler()) { BaseAddress = clientAddress, Timeout = Timeout.InfiniteTimeSpan };
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        }

        return client;
    }

    /// <summary>A handler that never follows redirects and never stores cookies.</summary>
    public SocketsHttpHandler CreateHandler()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new() { AllowAutoRedirect = false, UseCookies = false };
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}

/// <summary>Issues API keys through the internal listener.</summary>
public static class ApiKeyIssuer
{
    /// <summary>Issues a key of <paramref name="organization"/> with <paramref name="scopes"/> and returns its secret.</summary>
    public static Task<string> IssueAsync(this TerraBaseTestServer server, string organization, params string[] scopes) =>
        server.IssueNarrowedAsync(organization, project: null, database: null, scopes);

    /// <summary>Issues a key of <paramref name="organization"/> with <paramref name="scopes"/>, cancelled by <paramref name="cancellationToken"/>.</summary>
    public static Task<string> IssueAsync(this TerraBaseTestServer server, string organization, string[] scopes, CancellationToken cancellationToken) =>
        IssueCoreAsync(server, organization, project: null, database: null, scopes, cancellationToken);

    /// <summary>Issues a key of <paramref name="organization"/>, optionally narrowed, and returns its secret.</summary>
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
