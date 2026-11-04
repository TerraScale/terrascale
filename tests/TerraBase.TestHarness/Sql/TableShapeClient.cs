using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.Testing;

internal sealed record ShapeReply(HttpStatusCode Status, byte[] Body, JsonElement Json, string? Handle, string? ETag, string? CacheControl,
    string? Vary, string? NextOffset, string? UpToDate, string? Cursor, Uri? Location, Uri Target)
{
    public string? Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error)
        ? error.GetProperty("code").GetString()
        : null;

    public IEnumerable<JsonElement> Ops => Json.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray());
}

internal sealed class TableShapeClient(HttpClient http, string database, string table, string filter = "")
{
    public Dictionary<string, JsonObject> State { get; } = [with(StringComparer.Ordinal)];

    public string NextOffset { get; private set; } = "-1";

    public string? Handle { get; private set; }

    public string Path => $"{database}/tables/{table}/shape";

    public async Task<ShapeReply> SyncAsync()
    {
        var bootstrap = await GetAvailableAsync(new Uri($"{Path}?offset=-1{filter}", UriKind.Relative));
        if (bootstrap.Status != HttpStatusCode.TemporaryRedirect)
        {
            throw new InvalidOperationException($"bootstrap answered {bootstrap.Status}: {System.Text.Encoding.UTF8.GetString(bootstrap.Body)}");
        }

        State.Clear();
        Handle = bootstrap.Handle;
        Apply(await GetAvailableAsync(bootstrap.Location!));
        return await CatchUpAsync();
    }

    public async Task<ShapeReply> CatchUpAsync()
    {
        ShapeReply reply;
        do
        {
            reply = await ReadAsync(NextOffset);
            if (reply.Status != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"offset {NextOffset} answered {reply.Status}: {System.Text.Encoding.UTF8.GetString(reply.Body)}");
            }
        }
        while (reply.UpToDate != "true");
        return reply;
    }

    public async Task<ShapeReply> ReadAsync(string offset)
    {
        var reply = await GetAvailableAsync(Uri(offset, live: false));
        Apply(reply);
        return reply;
    }

    public async Task<ShapeReply> LiveAsync(string offset, string? cursor = null)
    {
        var reply = await GetAsync(Uri(offset, live: true, cursor));
        Apply(reply);
        return reply;
    }

    public Uri Uri(string offset, bool live, string? cursor = null, string? handle = null) =>
        new($"{Path}?handle={handle ?? Handle}&offset={offset}" + (live ? "&live=true" : "") + (cursor is null ? "" : "&cursor=" + cursor) + filter, UriKind.Relative);

    // The shape contract permits 429 while another database seals its snapshot. Keep the same
    // request and state, then retry with bounded backoff; raw GetAsync still exposes every refusal.
    private async Task<ShapeReply> GetAvailableAsync(Uri target)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var reply = await GetAsync(target);
        for (var attempt = 0; attempt < 6 && reply.Status == HttpStatusCode.TooManyRequests && reply.Code == "shape_busy"; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10 << attempt), token);
            reply = await GetAsync(target);
        }

        return reply;
    }

    public async Task<ShapeReply> GetAsync(Uri target, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var token = TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;
        using var response = await http.SendAsync(request, token);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        JsonElement json = default;
        if (bytes.Length > 0 && bytes[0] == '{')
        {
            json = JsonElement.Parse(bytes);
        }

        return new ShapeReply(response.StatusCode, bytes, json, Header(response, "TB-Shape-Handle"), response.Headers.ETag?.ToString(),
            Header(response, "Cache-Control"), Header(response, "Vary"), Header(response, "TB-Shape-Next-Offset"),
            Header(response, "TB-Shape-Up-To-Date"), Header(response, "TB-Shape-Cursor"), response.Headers.Location, target);
    }

    public void Apply(ShapeReply reply)
    {
        if (reply.Status != HttpStatusCode.OK)
        {
            return;
        }

        foreach (var op in reply.Ops)
        {
            var key = op.GetProperty("key").GetString()!;
            switch (op.GetProperty("op").GetString())
            {
                case "insert" or "update":
                    State[key] = JsonNode.Parse(op.GetProperty("row").GetRawText())!.AsObject();
                    break;
                case "delete":
                    State.Remove(key);
                    break;
                default:
                    throw new InvalidDataException("a shape operation has an unknown op");
            }
        }

        NextOffset = reply.NextOffset ?? NextOffset;
        Handle = reply.Handle ?? Handle;
    }

    public static async Task<Dictionary<string, JsonObject>> TruthAsync(SqlNativeDatabase db, string table, string select = "*", string where = "1")
    {
        var result = (await db.OkAsync($"SELECT rowid AS shape_key, {select} FROM {table} WHERE {where} ORDER BY rowid"))[0];
        var columns = result.GetProperty("columns").EnumerateArray()
            .Select(column => column.ValueKind == JsonValueKind.Object ? column.GetProperty("name").GetString()! : column.GetString()!).ToArray();
        Dictionary<string, JsonObject> rows = [with(StringComparer.Ordinal)];
        foreach (var row in result.GetProperty("rows").EnumerateArray())
        {
            var values = row.EnumerateArray().ToArray();
            var item = new JsonObject();
            for (var index = 1; index < columns.Length; index++)
            {
                item[columns[index]] = JsonNode.Parse(values[index].GetRawText());
            }

            rows[values[0].GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)] = item;
        }

        return rows;
    }

    public bool Equals(Dictionary<string, JsonObject> truth) =>
        State.Count == truth.Count && truth.All(row => State.TryGetValue(row.Key, out var mine) && JsonNode.DeepEquals(mine, row.Value));

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? values.SingleOrDefault()
        : response.Content.Headers.NonValidated.TryGetValues(name, out var content) ? content.SingleOrDefault() : null;
}
