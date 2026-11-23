using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.Benchmark;

internal enum ConsistencyMode
{
    Fast,
    Strong,
}

internal sealed record ConsistentRun(int Rows, string Bookmark, string Consistency);

internal sealed class ConsistencyChannel(HttpClient http, Uri executeUri, string accessToken)
{
    public const string BookmarkHeader = "TerraScale-Bookmark";
    public const string ConsistencyHeader = "TerraScale-Consistency";

    public string Bookmark { get; private set; } = "";

    public static string Wire(ConsistencyMode mode) => mode switch
    {
        ConsistencyMode.Fast => "fast",
        ConsistencyMode.Strong => "strong",
        _ => throw new UnreachableException(),
    };

    public async Task<ConsistentRun> ExecuteAsync(ConsistencyMode mode, string sql, IReadOnlyList<object> args, bool causal, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, executeUri)
        {
            Content = new ByteArrayContent(Body(sql, args)) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(ConsistencyHeader, Wire(mode));
        if (causal && Bookmark.Length > 0)
        {
            request.Headers.TryAddWithoutValidation(BookmarkHeader, Bookmark);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"{Wire(mode)} execute returned {(int)response.StatusCode}: {Encoding.UTF8.GetString(payload)}");
        }

        var bookmark = Single(response, BookmarkHeader);
        var consistency = Single(response, ConsistencyHeader);
        if (!string.Equals(consistency, Wire(mode), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Requested {Wire(mode)} but TerraBase ran {consistency}.");
        }

        Bookmark = bookmark;
        using var document = JsonDocument.Parse(payload);
        var rows = 0;
        foreach (var result in document.RootElement.GetProperty("results").EnumerateArray())
        {
            rows += result.GetProperty("rows").GetArrayLength();
        }

        return new ConsistentRun(rows, bookmark, consistency);
    }

    private static string Single(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) && values.ToArray() is [var value]
            ? value
            : throw new InvalidOperationException($"The response carries no single {header} header.");

    private static byte[] Body(string sql, IReadOnlyList<object> args)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("statements");
            writer.WriteStartObject();
            writer.WriteString("sql", sql);
            writer.WriteStartArray("args");
            foreach (var arg in args)
            {
                switch (arg)
                {
                    case long number:
                        writer.WriteNumberValue(number);
                        break;
                    case string text:
                        writer.WriteStringValue(text);
                        break;
                    default:
                        throw new ArgumentException($"Unsupported SQL argument type {arg.GetType()}.", nameof(args));
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
