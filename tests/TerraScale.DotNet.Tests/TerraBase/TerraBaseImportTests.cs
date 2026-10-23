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
    public async Task AFailedValidationJobReadsEveryContractField()
    {
        var job = TerraBaseConnection.ReadResponse(Encoding.UTF8.GetBytes("""
            {"id":"01JA00000000000000000000J1","project":"01JA0000000000000000000001","database":"import-01ja00000000000000000000d1",
             "region":"gru","state":"failed","cancelRequested":false,
             "options":{"exclude":["main.\"Audit Log\"","main.users.notes"]},
             "source":{"format":"sqlite","bytes":8192,"sha256":"SHA","receivedBytes":8192,"staged":true,"retainedUntil":"2026-10-19T12:00:00Z"},
             "preview":{"id":"01JA00000000000000000000P1",
               "tables":[{"source":"main.users","collection":"users","excluded":false,"rows":2,"primaryKey":[],
                 "columns":[{"name":"name","sourceType":"TEXT","mapping":"integer+string","nullable":true,"excluded":false},
                            {"name":"notes","sourceType":"TEXT","mapping":"null","nullable":true,"excluded":true}],
                 "constraints":[{"kind":"foreign_key","name":"users_org_fk","columns":["org_id"],"references":"main.orgs(id)","enforced":false},
                                {"kind":"not_null","name":null,"columns":["name"],"references":null,"enforced":false}],
                 "issues":[{"severity":"warning","code":"mixed_storage_classes","column":"name","row":null,"message":"integer and text values"}]}],
               "skipped":[{"kind":"virtual_table","name":"main.docs_fts"},{"kind":"internal_table","name":"main.sqlite_sequence"}],
               "skippedTruncated":false,"blocking":0,"warnings":2},
             "confirmedPreviewId":"01JA00000000000000000000P1",
             "progress":{"tablesDone":1,"tablesTotal":1,"rowsWritten":2,"rowsTotal":2,"currentTable":null},
             "validation":{"passed":false,"tables":[{"source":"main.users","collection":"users","sourceRows":2,"documents":3,
               "sourceChecksum":"01","documentChecksum":"02","match":false}]},
             "error":{"code":"validation_failed","message":"main.users: 2 rows, 3 documents","retryable":false,"table":"main.users","row":null,"column":null},
             "createdAt":"2026-10-12T10:00:00Z","updatedAt":"2026-10-12T10:05:00Z","startedAt":"2026-10-12T10:01:00Z","finishedAt":"2026-10-12T10:05:00Z"}
            """.Replace("SHA", Sha, StringComparison.Ordinal)), TerraBaseJsonContext.Wire.TerraBaseImportJob, HttpStatusCode.OK);

        await Assert.That(job.State).IsEqualTo(TerraBaseImportState.Failed);
        await Assert.That(job.ConfirmedPreviewId).IsEqualTo(job.Preview!.Id);
        await Assert.That(job.Options.Exclude![0]).IsEqualTo("main.\"Audit Log\"");
        await Assert.That(job.Source.Format).IsEqualTo(TerraBaseImportFormat.Sqlite);
        await Assert.That(job.Source.ReceivedBytes).IsEqualTo(8192);
        await Assert.That(job.Source.RetainedUntil).IsEqualTo(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var table = job.Preview.Tables[0];
        await Assert.That(table.PrimaryKey).IsEmpty();
        await Assert.That(table.Excluded).IsFalse();
        await Assert.That(table.Columns[0].Mapping).IsEqualTo("integer+string");
        await Assert.That(table.Columns[1].Mapping).IsEqualTo("null");
        await Assert.That(table.Constraints[1].Name).IsNull();
        await Assert.That(table.Constraints[1].References).IsNull();
        await Assert.That(table.Issues[0].Row).IsNull();
        await Assert.That(job.Preview.Skipped[1].Kind).IsEqualTo("internal_table");
        await Assert.That(job.Progress!.CurrentTable).IsNull();
        await Assert.That(job.Validation!.Tables[0].Match).IsFalse();
        await Assert.That(job.Error!.Code).IsEqualTo("validation_failed");
        await Assert.That(job.FinishedAt).IsNotNull();
    }

    [Test]
    [Arguments("\"state\":\"ready\"", "\"state\":\"paused\"")]
    [Arguments("\"state\":\"ready\"", "\"state\":2")]
    [Arguments("\"format\":\"sqlite\"", "\"format\":\"mysql\"")]
    [Arguments(",\"error\":null", "")]
    [Arguments("\"confirmedPreviewId\":null,", "")]
    [Arguments("\"receivedBytes\":4096,", "")]
    [Arguments("\"exclude\":[]", "\"exclude\":[null]")]
    public async Task JobsOutsideTheContractAreInvalidResponses(string field, string replacement)
    {
        var json = Job("ready").Replace(field, replacement, StringComparison.Ordinal);
        await Assert.That(json).IsNotEqualTo(Job("ready"));
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => Task.FromResult(TerraBaseConnection.ReadResponse(
            Encoding.UTF8.GetBytes(json), TerraBaseJsonContext.Wire.TerraBaseImportJob, HttpStatusCode.OK)));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

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

    [Test]
    public async Task ImportRequestsAreSentOnce()
    {
        var sent = 0;
        using var http = Http((_, _) =>
        {
            sent++;
            return Task.FromResult(Respond(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"capacity","message":"retry later"}}"""));
        });
        var database = Client(http).Database(Project, Destination);
        var create = await Assert.ThrowsAsync<TerraBaseUnavailableException>(() => database.CreateImportAsync(TerraBaseImportFormat.Sqlite, 4096, Sha));
        var upload = await Assert.ThrowsAsync<TerraBaseUnavailableException>(() => database.UploadImportSourceAsync(0, new byte[] { 1 }));
        await Assert.That(create!.ErrorCode).IsEqualTo("capacity");
        await Assert.That(upload!.ErrorCode).IsEqualTo("capacity");
        await Assert.That(sent).IsEqualTo(2);
    }

    [Test]
    [Arguments(409, "import_exists", typeof(TerraBaseConflictException))]
    [Arguments(409, "destination_not_empty", typeof(TerraBaseConflictException))]
    [Arguments(409, "destination_replicated", typeof(TerraBaseConflictException))]
    [Arguments(409, "offset_mismatch", typeof(TerraBaseConflictException))]
    [Arguments(409, "source_required", typeof(TerraBaseConflictException))]
    [Arguments(409, "invalid_state", typeof(TerraBaseConflictException))]
    [Arguments(409, "preview_changed", typeof(TerraBaseConflictException))]
    [Arguments(409, "preview_blocked", typeof(TerraBaseConflictException))]
    [Arguments(413, "payload_too_large", typeof(TerraBaseValidationException))]
    [Arguments(400, "invalid_request", typeof(TerraBaseValidationException))]
    [Arguments(404, "unknown_import", typeof(TerraBaseNotFoundException))]
    [Arguments(403, "forbidden_scope", typeof(TerraBaseForbiddenException))]
    [Arguments(421, "wrong_region", typeof(TerraBaseWrongRegionException))]
    public async Task ImportErrorsKeepTheirContractCodes(int status, string code, Type type)
    {
        using var http = Http((_, _) =>
        {
            var response = Respond((HttpStatusCode)status, $$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""");
            response.Headers.Add("X-TerraBase-Database-Home", "gru");
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => Client(http).Database(Project, Destination).ResumeImportAsync());
        await Assert.That(error!.GetType()).IsEqualTo(type);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        if (error is TerraBaseWrongRegionException wrong) await Assert.That(wrong.HomeRegion).IsEqualTo("gru");
    }

    [Test]
    public async Task CallerCancellationStopsAnUploadPart()
    {
        using var http = Http(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Respond(HttpStatusCode.OK, Job("awaiting_source"));
        });
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Client(http).Database(Project, Destination).UploadImportSourceAsync(0, new byte[] { 1 }, caller.Token));
    }

    [Test]
    public async Task RequestsOutsideTheContractAreRejectedBeforeSending()
    {
        using var http = Http((_, _) => throw new InvalidOperationException("nothing may be sent"));
        var database = Client(http).Database(Project, Destination);
        await Assert.ThrowsAsync<ArgumentException>(() => database.CreateImportAsync(TerraBaseImportFormat.Sqlite, 1, Sha.ToUpperInvariant()));
        await Assert.ThrowsAsync<ArgumentException>(() => database.CreateImportAsync(TerraBaseImportFormat.Sqlite, 1, Sha[1..]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.CreateImportAsync(TerraBaseImportFormat.Sqlite, 0, Sha));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.CreateImportAsync(TerraBaseImportFormat.Sqlite, (1L << 30) + 1, Sha));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.UploadImportSourceAsync(-1, new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.UploadImportSourceAsync(0, ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.UploadImportSourceAsync(0, new byte[TerraBaseDatabase.MaxImportSourcePartBytes + 1]));
        await Assert.ThrowsAsync<ArgumentException>(() => database.StartImportAsync(""));
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
