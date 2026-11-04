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

    public static async Task<ImportClient> StartAsync(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null)
    {
        var api = await ManagementApi.StartAsync(services: services);
        var project = EntityIds.New().ToString();
        return new ImportClient(api, project, api.WithToken(Token(api, project, AllScopes)));
    }

    /// <summary>A client of the same server for another project, with every scope.</summary>
    public ImportClient For(string project) => new(Api, project, Api.WithToken(Token(Api, project, AllScopes)), ownsApi: false);

    /// <summary>A project access token for <paramref name="project"/> with <paramref name="scopes"/>.</summary>
    public static string Token(ManagementApi api, string project, string scopes, DateTime? expires = null) => api.Issuer.Mint(EntityIds.New().ToString(), token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = scopes;
        if (expires is { } expiration)
        {
            token.Expires = expiration;
        }
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

    public async Task<JsonElement> GetAsync(string import, CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(Uri(import), cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    /// <summary>Polls until the job reaches one of <paramref name="states"/>.</summary>
    public async Task<JsonElement> WaitAsync(string import, params string[] states)
    {
        JsonElement job = default;
        await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
        {
            job = await GetAsync(import, cancellationToken);
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

    /// <summary>
    /// The one document whose body holds every <paramref name="key"/> field (strings compared as text, other values as raw
    /// JSON), or Undefined when none does. Imported documents have generated ULIDs; source keys are plain fields.
    /// </summary>
    public async Task<JsonElement> DocumentAsync(string import, string collection, params (string Field, string Value)[] key) =>
        (await ItemsAsync(import, collection)).Select(item => item.GetProperty("document"))
            .SingleOrDefault(document => key.All(part => document.TryGetProperty(part.Field, out var value) && Text(value) == part.Value));

    /// <summary>The <paramref name="fields"/> of every document of a collection, joined with <c>:</c>.</summary>
    public async Task<List<string>> KeysAsync(string import, string collection, params string[] fields) =>
        [.. (await ItemsAsync(import, collection)).Select(item => string.Join(':', fields.Select(field => Text(item.GetProperty("document").GetProperty(field)))))];

    /// <summary>Every document id of a collection.</summary>
    public async Task<List<string>> IdsAsync(string import, string collection) =>
        [.. (await ItemsAsync(import, collection)).Select(item => item.GetProperty("id").GetString()!)];

    /// <summary>Whether <paramref name="id"/> is a canonical uppercase ULID.</summary>
    public static bool IsUlid(string id) => id.Length == 26 && id[0] is >= '0' and <= '7' && id.All("0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains);

    private async Task<List<JsonElement>> ItemsAsync(string import, string collection)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        do
        {
            using var response = await Client.GetAsync(Uri($"{Data(import)}/collections/{collection}/documents?limit=1000" + (cursor is null ? "" : "&cursor=" + System.Uri.EscapeDataString(cursor))));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return items;
            }

            var page = await ReadAsync(response);
            items.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);
        return items;
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

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

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return text.Length == 0 ? default : JsonElement.Parse(text);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
