using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseExportTests
{
    private const string First = "01JA00000000000000000000A1";
    private const string Second = "01JA00000000000000000000A2";

    [Test]
    public async Task Every_line_arrives_in_order_even_when_split_across_reads()
    {
        var body = $"{{\"collection\":\"items\",\"id\":\"{First}\",\"document\":{{\"name\":\"Ada\"}}}}\n"
            + $"{{\"collection\":\"users\",\"id\":\"{Second}\",\"document\":{{\"tags\":[1,2]}}}}\n";
        string? query = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            query = request.RequestUri!.Query;
            return Task.FromResult(Ndjson(new FragmentedStream(Encoding.UTF8.GetBytes(body), 7)));
        });
        using var http = new HttpClient(handler);
        var lines = new List<TerraBaseExportLine>();
        await foreach (var line in Database(http).ExportAsync()) lines.Add(line);
        await Assert.That(query).IsEqualTo(string.Empty);
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0].Cursor).IsEqualTo(new TerraBaseExportCursor("items", First));
        await Assert.That(lines[0].Document.GetProperty("name").GetString()).IsEqualTo("Ada");
        await Assert.That(lines[1].Collection).IsEqualTo("users");
        await Assert.That(lines[1].Document.GetProperty("tags").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task Resuming_sends_both_after_parameters()
    {
        string? query = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            query = request.RequestUri!.Query;
            return Task.FromResult(Ndjson(new MemoryStream()));
        });
        using var http = new HttpClient(handler);
        await DrainAsync(Database(http).ExportAsync(new TerraBaseExportCursor("my items", First)));
        await Assert.That(query).IsEqualTo($"?after_collection=my%20items&after_id={First}");
    }

    [Test]
    public async Task A_final_line_without_its_newline_is_an_incomplete_export()
    {
        var body = $"{{\"collection\":\"items\",\"id\":\"{First}\",\"document\":{{}}}}\n{{\"collection\":\"items\"";
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Ndjson(new MemoryStream(Encoding.UTF8.GetBytes(body)))));
        using var http = new HttpClient(handler);
        var lines = 0;
        var error = await Assert.ThrowsAsync<TerraBaseException>(async () =>
        {
            await foreach (var _ in Database(http).ExportAsync()) lines++;
        });
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
        await Assert.That(lines).IsEqualTo(1);
    }

    [Test]
    public async Task Errors_map_to_typed_exceptions()
    {
        using var handler = new TerraBaseTestHandler((_, _) =>
            Task.FromResult(TerraBaseIntegrationFixture.Json("""{"error":{"code":"unknown_database","message":"unknown database"}}""", HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => DrainAsync(Database(http).ExportAsync()));
        await Assert.That(error!.ErrorCode).IsEqualTo("unknown_database");
    }

    [Test]
    [Arguments("42", "42")]
    [Arguments("customer/key ?&=+#%", "customer%2Fkey%20%3F%26%3D%2B%23%25")]
    [Arguments("[42,\"customer\"]", "%5B42%2C%22customer%22%5D")]
    [Arguments("客户", "%E5%AE%A2%E6%88%B7")]
    public async Task A_returned_sql_key_can_resume_export_verbatim(string id, string encodedId)
    {
        var body = JsonSerializer.Serialize(new { collection = "items", id, document = new { value = 1 } }) + "\n";
        var sends = 0;
        string? query = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sends++;
            if (sends == 1)
                return Task.FromResult(Ndjson(new FragmentedStream(Encoding.UTF8.GetBytes(body), 3)));
            query = request.RequestUri!.Query;
            return Task.FromResult(Ndjson(new MemoryStream()));
        });
        using var http = new HttpClient(handler);
        TerraBaseExportCursor? cursor = null;
        await foreach (var line in Database(http).ExportAsync())
        {
            await Assert.That(line.Id).IsEqualTo(id);
            await Assert.That(line.Document.GetProperty("value").GetInt32()).IsEqualTo(1);
            cursor = line.Cursor;
        }
        await Assert.That(cursor).IsNotNull();
        await DrainAsync(Database(http).ExportAsync(cursor));
        await Assert.That(query).IsEqualTo("?after_collection=items&after_id=" + encodedId);
        await Assert.That(sends).IsEqualTo(2);
    }

    [Test]
    [Arguments(1)]
    [Arguments(256)]
    public async Task Stored_resume_key_length_boundaries_are_accepted(int length)
    {
        var id = new string('x', length);
        string? query = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            query = request.RequestUri!.Query;
            return Task.FromResult(Ndjson(new MemoryStream()));
        });
        using var http = new HttpClient(handler);
        await DrainAsync(Database(http).ExportAsync(new TerraBaseExportCursor("items", id)));
        await Assert.That(query).IsEqualTo("?after_collection=items&after_id=" + id);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("a\nb")]
    [Arguments("a\0b")]
    [Arguments("a\u007fb")]
    [Arguments("a\u0085b")]
    public async Task Invalid_resume_keys_are_rejected_before_any_request(string? id)
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => DrainAsync(Database(http).ExportAsync(new TerraBaseExportCursor("items", id!))));
    }

    [Test]
    public async Task An_oversized_resume_key_is_rejected_before_any_request()
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => DrainAsync(Database(http).ExportAsync(new TerraBaseExportCursor("items", new string('x', 257)))));
    }

    [Test]
    public async Task A_sql_key_still_cannot_address_a_document_point_operation()
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Database(http).GetDocumentAsync("items", "42"));
    }

    [Test]
    [Arguments(65535, true)]
    [Arguments(65536, true)]
    [Arguments(65537, true)]
    [Arguments(65537, false)]
    [Arguments(65536, false)]
    public async Task Export_checks_completed_and_incomplete_record_bounds(int lineBytes, bool newline)
    {
        const int limit = 64 * 1024;
        var prefix = $"{{\"collection\":\"items\",\"id\":\"{First}\",\"document\":{{\"padding\":\"";
        const string suffix = "\"}}";
        var padding = new string('x', lineBytes - prefix.Length - suffix.Length);
        // Sixteen reads reach the limit; the next read carries the excess byte and newline together.
        var source = new FragmentedStream(Encoding.UTF8.GetBytes(prefix + padding + suffix + (newline ? "\n" : "")), 4096);
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Ndjson(source)));
        using var http = new HttpClient(handler);
        var database = new TerraBaseDocumentsClient(new TerraBaseConnection(
            http, null, null, null, "credential", TerraBaseConnection.RequestDeadline, limit,
            _ => ValueTask.FromResult("test-token"), region: "sjc"))
            .Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var lines = new List<TerraBaseExportLine>();
        async Task ReadAsync()
        {
            await foreach (var line in database.ExportAsync()) lines.Add(line);
        }

        if (newline && lineBytes <= limit)
        {
            await ReadAsync();
            await Assert.That(lines.Count).IsEqualTo(1);
            await Assert.That(lines[0].Document.GetProperty("padding").GetString()).IsEqualTo(padding);
            await Assert.That(lines[0].Cursor).IsEqualTo(new TerraBaseExportCursor("items", First));
        }
        else
        {
            var error = await Assert.ThrowsAsync<TerraBaseException>(ReadAsync);
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
            await Assert.That(lines.Count).IsEqualTo(0);
        }
        await Assert.That(source.Disposed).IsTrue();
    }

    private static TerraBaseDatabase Database(HttpClient http) => TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

    private static async Task DrainAsync(IAsyncEnumerable<TerraBaseExportLine> lines)
    {
        await foreach (var _ in lines) { }
    }

    private static HttpResponseMessage Ndjson(Stream source)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
        return response;
    }

    private sealed class FragmentedStream(byte[] bytes, int fragment) : MemoryStream(bytes)
    {
        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], cancellationToken);
    }
}
