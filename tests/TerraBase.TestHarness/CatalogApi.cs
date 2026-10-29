using System.Net;
using System.Text.Json;

namespace TerraBase.Testing;

internal static class CatalogApi
{
    public static async Task<ManagedDatabaseRef> CreateAsync(HttpClient client, string user, string name, IReadOnlyList<string> replicas, CancellationToken cancellationToken = default)
    {
        var organization = await CreateResourceAsync(client, user, "/v1/management/organizations", new { name = "fixture" }, cancellationToken);
        var root = $"/v1/management/organizations/{organization}";
        using (var plan = await SendAsync(client, user, HttpMethod.Put, root + "/plan", new { plan = "enterprise" }, cancellationToken))
        {
            plan.EnsureSuccessStatusCode();
        }
        var project = await CreateResourceAsync(client, user, root + "/projects", new { name = "fixture" }, cancellationToken);
        var database = await CreateResourceAsync(client, user, root + $"/projects/{project}/databases", new { name, replicaRegions = replicas }, cancellationToken);
        return new ManagedDatabaseRef(organization, project, database, name);
    }

    public static async Task<string> CreateResourceAsync(HttpClient client, string user, string path, object body, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(client, user, HttpMethod.Post, path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString()!;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string user, HttpMethod method, string path, object? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add("X-TerraBase-User-Id", user);
        return await client.SendAsync(request, cancellationToken);
    }
}
