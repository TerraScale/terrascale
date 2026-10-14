using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

/// <summary>Document calls against one region, and the canonical form the suite compares documents by.</summary>
internal static class Docs
{
    // The largest page the list endpoint takes (QueryParser.MaxLimit).
    private const int ListPage = 1000;

    // Members sorted at every depth: equal documents compare equal whatever order a region stored them in.
    public static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(',', obj.OrderBy(member => member.Key, StringComparer.Ordinal)
            .Select(member => JsonSerializer.Serialize(member.Key) + ":" + Canonical(member.Value))) + "}",
        JsonArray array => "[" + string.Join(',', array.Select(Canonical)) + "]",
        _ => node?.ToJsonString() ?? "null",
    };

    public static async Task PutAsync(HttpClient client, Uri uri, JsonNode body, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.PutAsync(uri, Json(body, "application/json"), token), token: token);
        await EnsureSuccessAsync(response);
    }

    // PUT that is also sent again after 503 remote_region_unavailable or confirmation_busy, which the write route answers
    // before it executes anything; write_outcome_unknown (or any other 503) is never sent again. Answers the write's
    // durability as the region acknowledged it (remote_durable, local_durable), or null when the answer names none.
    public static async Task<string?> PutRefusedBeforeRunAsync(HttpClient client, Uri uri, JsonNode body, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.PutAsync(uri, Json(body, "application/json"), token), refusedBeforeRun: true, token: token);
        return (await BodyAsync(response, token))?["durability"]?.GetValue<string>();
    }

    public static async Task PatchAsync(HttpClient client, Uri uri, JsonNode merge, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.PatchAsync(uri, Json(merge, "application/merge-patch+json"), token), token: token);
        await EnsureSuccessAsync(response);
    }

    // Counter and set ops (application/vnd.terrabase.ops+json); answers the document after the ops.
    public static async Task<JsonNode?> OpsAsync(HttpClient client, Uri uri, params JsonObject[] ops)
    {
        using var response = await AdmittedAsync(() => client.PatchAsync(uri, Json(new JsonObject { ["ops"] = new JsonArray(ops) }, "application/vnd.terrabase.ops+json")));
        return await BodyAsync(response);
    }

    // A POST with a JSON body; answers the JSON body, or null when there is none.
    public static async Task<JsonNode?> PostAsync(HttpClient client, Uri uri, JsonNode body, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.PostAsync(uri, Json(body, "application/json"), token), token: token);
        return await BodyAsync(response, token);
    }

    public static async Task DeleteAsync(HttpClient client, Uri uri, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.DeleteAsync(uri, token), token: token);
        await EnsureSuccessAsync(response);
    }

    // The document as this region serves it, or null while it is absent.
    public static async Task<JsonNode?> ReadAsync(HttpClient client, Uri uri, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.GetAsync(uri, token), token: token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return JsonNode.Parse(json.RootElement.GetProperty("document").GetRawText());
    }

    // A JSON body read with GET, or null when the answer is 404.
    public static async Task<JsonNode?> GetJsonAsync(HttpClient client, Uri uri, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.GetAsync(uri, token), token: token);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await BodyAsync(response, token);
    }

    // A deleted document's answer: 404 with its retained tombstone's ETag, or null for a document never written (or collected).
    public static async Task<string?> TombstoneAsync(HttpClient client, Uri uri, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.GetAsync(uri, token), token: token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return response.Headers.ETag?.Tag;
        }

        await EnsureSuccessAsync(response);
        throw new HttpRequestException($"GET {uri} answered {(int)response.StatusCode}, not the deleted document's 404", null, response.StatusCode);
    }

    public static async Task<HttpStatusCode> StatusAsync(HttpClient client, Uri uri, CancellationToken token = default)
    {
        using var response = await AdmittedAsync(() => client.GetAsync(uri, token), token: token);
        return response.StatusCode;
    }

    // Every document of the test's collection as this region serves it.
    public static async Task<Listing> ListAsync(HttpClient client, Cluster cluster, CancellationToken token = default)
    {
        var documents = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var cursorQuery = cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}";
            var uri = new Uri($"{cluster.DatabasePath}/collections/{cluster.Collection}/documents?limit={ListPage}{cursorQuery}", UriKind.Relative);
            using var response = await AdmittedAsync(() => client.GetAsync(uri, token), token: token);
            await EnsureSuccessAsync(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                documents.Add(item.GetProperty("id").GetString()!, Canonical(JsonNode.Parse(item.GetProperty("document").GetRawText())));
            }

            cursor = json.RootElement.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        return new Listing(documents.Count, Digest(documents.Select(document => (document.Key, document.Value))));
    }

    // SHA-256 over "id, tab, canonical document, newline" lines in id order.
    public static string Digest(IEnumerable<(string Id, string Canonical)> documents)
    {
        var text = new StringBuilder();
        foreach (var (id, canonical) in documents.OrderBy(document => document.Id, StringComparer.Ordinal))
        {
            _ = text.Append(id).Append('\t').Append(canonical).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static async Task<JsonNode?> BodyAsync(HttpResponseMessage response, CancellationToken token = default)
    {
        await EnsureSuccessAsync(response);
        var text = await response.Content.ReadAsStringAsync(token);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    // A failed call reports its method, address and answer, not only a status code.
    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var answer = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}: {answer}", null, response.StatusCode);
    }

    // Tenant admission answers 429 with Retry-After before it runs the request, so sending it again cannot repeat it.
    // Past the convergence deadline the 429 is returned, and the caller reports it.
    private static async Task<HttpResponseMessage> AdmittedAsync(Func<Task<HttpResponseMessage>> send, bool refusedBeforeRun = false, CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var response = await send();
            var refused = response.StatusCode == HttpStatusCode.TooManyRequests
                || (refusedBeforeRun && response.StatusCode == HttpStatusCode.ServiceUnavailable
                    && await response.Content.ReadAsStringAsync(token) is var answer
                    && (answer.Contains("\"remote_region_unavailable\"", StringComparison.Ordinal) || answer.Contains("\"confirmation_busy\"", StringComparison.Ordinal)));
            if (!refused || clock.Elapsed > Converge.Deadline)
            {
                return response;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            response.Dispose();
            await Task.Delay(wait, token);
        }
    }

    // A fresh body per send: a retried request needs its content again.
    private static StringContent Json(JsonNode body, string mediaType) => new(body.ToJsonString(), Encoding.UTF8, mediaType);
}

/// <summary>A region's view of a collection: its document count and the digest of its canonical documents.</summary>
internal sealed record Listing(int Count, string Digest);
