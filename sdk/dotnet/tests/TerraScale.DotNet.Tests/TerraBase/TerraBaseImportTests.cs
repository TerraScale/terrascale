using System.Net;
using System.Text;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>
/// Import contract v1 revision 3 wire boundaries (TerraScale/terrascale docs/terrabase/import.md and ImportModels.cs on
/// feat/postgres-turso-import-engine at 567bfd33) over synthetic fixtures; no simulated server and no real export.
/// </summary>
public sealed class TerraBaseImportTests
{
    private const string Project = "01JA0000000000000000000001";
    private const string Destination = "import-01ja00000000000000000000d1";
    private static readonly string Sha = new('a', 64);
    private static readonly Uri Origin = new("https://terrabase.example.test/");

    [Test]
    public async Task OperationsSendTheContractRequests()
    {
        var sent = new List<string>();
        var credentials = new List<string?>();
        using var http = Http(async (request, token) =>
        {
            var content = request.Content;
            sent.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {content?.Headers.ContentType?.MediaType} "
                + (content is null ? null : await content.ReadAsStringAsync(token)));
            credentials.Add(request.Headers.Authorization?.ToString());
            return Respond(HttpStatusCode.OK, Job("ready"));
        });
        var database = Client(http).Database(Project, Destination);

        await database.CreateImportAsync(TerraBaseImportFormat.Ndjson, 4096, Sha,
            new TerraBaseImportOptions(["main.audit_log"], "users"));
        await database.CreateImportAsync(TerraBaseImportFormat.Sqlite, 1, Sha);
        await database.UploadImportSourceAsync(TerraBaseDatabase.MaxImportSourcePartBytes, "ab"u8.ToArray());
        await database.GetImportAsync();
        await database.UpdateImportAsync(new TerraBaseImportOptions(Exclude: ["main.logs"]));
        await database.StartImportAsync("01JA00000000000000000000P1");
        await database.CancelImportAsync();
        await database.ResumeImportAsync();

        var job = $"/v1/projects/{Project}/databases/{Destination}/import";
        await Assert.That(string.Join('\n', sent)).IsEqualTo(string.Join('\n',
            $$$"""POST {{{job}}} application/json {"format":"ndjson","sourceBytes":4096,"sourceSha256":"{{{Sha}}}","options":{"exclude":["main.audit_log"],"collection":"users"}}""",
            $$$"""POST {{{job}}} application/json {"format":"sqlite","sourceBytes":1,"sourceSha256":"{{{Sha}}}"}""",
            $"PUT {job}/source?offset=4194304 application/octet-stream ab",
            $"GET {job}  ",
            $$$"""PATCH {{{job}}} application/json {"options":{"exclude":["main.logs"]}}""",
            $$$"""POST {{{job}}}/start application/json {"previewId":"01JA00000000000000000000P1"}""",
            $"POST {job}/cancel  ",
            $"POST {job}/resume  "));
        await Assert.That(credentials.Distinct().Single()).IsEqualTo("Bearer project-access-token");
    }

    private static string Job(string state) => $$"""
        {"id":"01JA00000000000000000000J1","project":"{{Project}}","database":"{{Destination}}","region":"gru","state":"{{state}}",
         "cancelRequested":false,"options":{"exclude":[]},
         "source":{"format":"sqlite","bytes":4096,"sha256":"{{Sha}}","receivedBytes":4096,"staged":true,"retainedUntil":null},
         "preview":null,"confirmedPreviewId":null,"progress":null,"validation":null,"error":null,
         "createdAt":"2026-10-12T10:00:00Z","updatedAt":"2026-10-12T10:00:00Z","startedAt":null,"finishedAt":null}
        """;

    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new TerraBaseTestHandler(send));

    private static HttpResponseMessage Respond(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TerraBaseDocumentsClient Client(HttpClient http) => new(http, new TerraBaseDocumentsClientOptions
    {
        BaseAddress = Origin,
        AccessTokenProvider = _ => ValueTask.FromResult("project-access-token"),
    });
}
