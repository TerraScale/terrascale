using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.Testing;

/// <summary>
/// Drives the import API (docs/terrabase/import.md) like the dashboard and the SDK: a fresh project and token, a new
/// destination from createDatabase, part uploads, polling. Each test gets its own server and project.
/// </summary>
public sealed class ImportClient : IAsyncDisposable
{
    public const string AllScopes = "databases:read databases:write documents:read documents:write";

    private readonly bool _ownsApi;

    private ImportClient(ManagementApi api, string project, HttpClient client, bool ownsApi = true)
    {
        Api = api;
        Project = project;
        Client = client;
        _ownsApi = ownsApi;
    }

    public ManagementApi Api { get; }

    public string Project { get; }

    /// <summary>A client with a project access token holding every scope.</summary>
    public HttpClient Client { get; }

    public static async Task<ImportClient> StartAsync()
    {
        var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        return new ImportClient(api, project, api.WithToken(Token(api, project, AllScopes)));
    }

    /// <summary>A client of the same server for another project, with every scope.</summary>
    public ImportClient For(string project) => new(Api, project, Api.WithToken(Token(Api, project, AllScopes)), ownsApi: false);

    /// <summary>A project access token for <paramref name="project"/> with <paramref name="scopes"/>.</summary>
    public static string Token(ManagementApi api, string project, string scopes) => api.Issuer.Mint(EntityIds.New().ToString(), token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = scopes;
    });

    /// <summary>Creates a new destination the documented way and returns its import route.</summary>
    public async Task<string> CreateDestinationAsync()
    {
        var name = "import-" + EntityIds.New().ToString().ToLowerInvariant();
        using var created = await Client.PostAsJsonAsync(Uri($"/v1/projects/{Project}/databases"), new { name });
        if (created.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"createDatabase answered {created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        }

        return $"/v1/projects/{Project}/databases/{name}/import";
    }

    /// <summary>The data route of the destination behind <paramref name="import"/>.</summary>
    public static string Data(string import) => import[..^"/import".Length];

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public async Task<(HttpStatusCode Status, JsonElement Body)> CreateAsync(string import, string format, byte[] source, object? options = null) =>
        await SendAsync(HttpMethod.Post, import, new { format, sourceBytes = source.LongLength, sourceSha256 = Sha256(source), options });

    /// <summary>Uploads <paramref name="source"/> from <paramref name="offset"/> in parts of <paramref name="part"/> bytes; returns the last answer.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(string import, byte[] source, int part = 4 * 1024 * 1024, long offset = 0)
    {
        (HttpStatusCode, JsonElement) last = default;
        for (var at = offset; at < source.LongLength; at += part)
        {
            last = await PartAsync(import, at, source.AsMemory((int)at, (int)Math.Min(part, source.LongLength - at)).ToArray());
            if (last.Item1 != HttpStatusCode.OK)
            {
                return last;
            }
        }

        return last;
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PartAsync(string import, long offset, byte[] bytes)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await Client.PutAsync(Uri($"{import}/source?offset={offset}"), content);
        return (response.StatusCode, await ReadAsync(response));
    }

    public async Task<JsonElement> GetAsync(string import)
    {
        using var response = await Client.GetAsync(Uri(import));
        return await ReadAsync(response);
    }

    /// <summary>Polls until the job reaches one of <paramref name="states"/>.</summary>
    public async Task<JsonElement> WaitAsync(string import, params string[] states)
    {
        JsonElement job = default;
        await TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            job = await GetAsync(import);
            return states.Contains(job.GetProperty("state").GetString());
        }, TimeSpan.FromSeconds(120));
        return job;
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> StartAsync(string import, string? previewId) =>
        SendAsync(HttpMethod.Post, import + "/start", new { previewId });

    public Task<(HttpStatusCode Status, JsonElement Body)> UpdateAsync(string import, object options) =>
        SendAsync(HttpMethod.Patch, import, new { options });

    public Task<(HttpStatusCode Status, JsonElement Body)> CancelAsync(string import) => SendAsync(HttpMethod.Post, import + "/cancel", null);

    public Task<(HttpStatusCode Status, JsonElement Body)> ResumeAsync(string import) => SendAsync(HttpMethod.Post, import + "/resume", null);

    /// <summary>A document of the destination, or Undefined when absent.</summary>
    public async Task<JsonElement> DocumentAsync(string import, string collection, string id)
    {
        using var response = await Client.GetAsync(Uri($"{Data(import)}/collections/{collection}/documents/by-key?id={System.Uri.EscapeDataString(id)}"));
        return response.StatusCode == HttpStatusCode.OK ? (await ReadAsync(response)).GetProperty("document") : default;
    }

    /// <summary>Every document id of a collection.</summary>
    public async Task<List<string>> IdsAsync(string import, string collection)
    {
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            using var response = await Client.GetAsync(Uri($"{Data(import)}/collections/{collection}/documents?limit=1000" + (cursor is null ? "" : "&cursor=" + System.Uri.EscapeDataString(cursor))));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ids;
            }

            var page = await ReadAsync(response);
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            cursor = page.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);
        return ids;
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, Uri(path)) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await Client.SendAsync(request);
        return (response.StatusCode, await ReadAsync(response));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_ownsApi)
        {
            await Api.DisposeAsync();
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonElement.Parse(text);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
