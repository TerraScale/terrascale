using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Documents;

/// <summary>A response read to the end: status, JSON body (Undefined when empty) and the headers tests check.</summary>
internal sealed record ApiResponse(HttpStatusCode Status, JsonElement Json, string? ETag, Uri? Location)
{
    public string? Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error)
        ? error.GetProperty("code").GetString()
        : null;

    public int? Op => Json.TryGetProperty("error", out var error) && error.TryGetProperty("op", out var op) ? op.GetInt32() : null;

    public string Version => Json.GetProperty("version").GetString()!;

    public IReadOnlyList<string> Ids => [.. Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)];
}

/// <summary>
/// One test's own server with a client-listener key, database <c>db</c> and collection
/// <c>items</c>, plus helpers that send JSON and read whole responses.
/// </summary>
internal sealed class DocumentApi : IAsyncDisposable
{
    public const string Database = "db";
    public const string Collection = "items";
    public const string Documents = $"/v1/databases/{Database}/collections/{Collection}/documents";
    public const string Query = $"/v1/databases/{Database}/collections/{Collection}/query";
    public const string Transactions = $"/v1/databases/{Database}/transactions";

    private readonly ClientTls _tls;

    private DocumentApi(ClientTls tls, TerraBaseTestServer server, HttpClient client)
    {
        _tls = tls;
        Server = server;
        Client = client;
    }

    public TerraBaseTestServer Server { get; }

    public HttpClient Client { get; }

    /// <summary>Starts a server and creates <c>db</c> and <c>items</c> with a read and write key.</summary>
    public static async Task<DocumentApi> StartAsync()
    {
        var tls = new ClientTls();
        TerraBaseTestServer? server = null;
        HttpClient? client = null;
        try
        {
            server = await TerraBaseTestServer.StartAsync(tls.Apply);
            client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:read", "documents:write"));
            var api = new DocumentApi(tls, server, client);
            (await api.SendAsync(HttpMethod.Put, $"/v1/databases/{Database}")).Status.ShouldBe(HttpStatusCode.Created);
            (await api.SendAsync(HttpMethod.Post, $"/v1/databases/{Database}/collections", new { name = Collection })).Status.ShouldBe(HttpStatusCode.Created);
            return api;
        }
        catch
        {
            client?.Dispose();
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            tls.Dispose();
            throw;
        }
    }

    /// <summary>A new document id.</summary>
    public static string NewId() => Guid.CreateVersion7().ToString();

    public static string Document(string id) => $"{Documents}/{id}";

    /// <summary>A client for the same server with other scopes.</summary>
    public async Task<HttpClient> ClientWithScopesAsync(params string[] scopes) =>
        _tls.CreateClient(Server.ClientAddress!, await Server.IssueAsync(scopes));

    public Task<ApiResponse> PutAsync(string id, object document, Action<HttpRequestMessage>? configure = null) =>
        SendAsync(HttpMethod.Put, Document(id), document, configure);

    public Task<ApiResponse> GetAsync(string id, Action<HttpRequestMessage>? configure = null) =>
        SendAsync(HttpMethod.Get, Document(id), configure: configure);

    public Task<ApiResponse> QueryAsync(object query) => SendAsync(HttpMethod.Post, Query, query);

    public Task<ApiResponse> TransactAsync(params object[] ops) => SendAsync(HttpMethod.Post, Transactions, new { ops });

    public Task<ApiResponse> SendAsync(HttpMethod method, string uri, object? body = null, Action<HttpRequestMessage>? configure = null) =>
        SendAsync(Client, method, uri, body is null ? null : JsonContent.Create(body), configure);

    public Task<ApiResponse> SendRawAsync(HttpMethod method, string uri, string body, string mediaType, Action<HttpRequestMessage>? configure = null) =>
        SendAsync(Client, method, uri, new StringContent(body, Encoding.UTF8, mediaType), configure);

    public static async Task<ApiResponse> SendAsync(
        HttpClient client, HttpMethod method, string uri, HttpContent? content = null, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative)) { Content = content };
        configure?.Invoke(request);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (text.Length > 0)
        {
            using var document = JsonDocument.Parse(text);
            json = document.RootElement.Clone();
        }

        return new ApiResponse(response.StatusCode, json, response.Headers.ETag?.Tag.Trim('"'), response.Headers.Location);
    }

    public static Action<HttpRequestMessage> IfMatch(string version) =>
        request => request.Headers.IfMatch.Add(version == "*" ? EntityTagHeaderValue.Any : new EntityTagHeaderValue($"\"{version}\""));

    public static Action<HttpRequestMessage> IfNoneMatch(string tag) =>
        request => request.Headers.IfNoneMatch.Add(tag == "*" ? EntityTagHeaderValue.Any : new EntityTagHeaderValue($"\"{tag}\""));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Server.DisposeAsync();
        _tls.Dispose();
    }
}

internal static class StatusAssertions
{
    /// <summary>Fails setup early when a precondition request does not return <paramref name="expected"/>.</summary>
    public static void ShouldBe(this HttpStatusCode actual, HttpStatusCode expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"expected {expected}, got {actual}");
        }
    }
}
