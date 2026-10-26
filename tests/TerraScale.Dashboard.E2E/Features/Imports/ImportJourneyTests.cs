using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Imports;

// Guided imports through the running dashboard, its official SDK import client and the test-only
// TerraBase import producer for the current generated-ULID contract. They qualify the dashboard flow;
// TerraBase's engine and real exports are qualified by the core owner's integration tests.
[ClassDataSource<DashboardRootFixture>]
public sealed class ImportJourneyTests(DashboardRootFixture fixture)
{
    private const int Part = 4 * 1024 * 1024;

    [Test]
    public async Task PlainNdjsonTargetSurvivesExclusionEditsAndImportsOriginalObjects()
    {
        await using var scenario = await ImportScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var page = await HtmlAsync(client, "/imports");
        await Assert.That(page).Contains("Target collection (optional)");
        await Assert.That(page).Contains("JSON arrays and CSV are not accepted");
        var name = Minted(page);
        var csrf = await HttpJourney.CsrfAsync(client, "/imports");
        var source = Encoding.UTF8.GetBytes("{\"name\":\"Ada\"}\n{\"name\":\"Grace\"}\n");
        var form = Form(csrf, name, "ndjson", source);
        form["collection"] = "people_test";
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/imports", form);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await JsonAsync(created)).GetProperty("options").GetProperty("collection").GetString()).IsEqualTo("people_test");
        await ControlAsync(client, name, new { command = "manual" });
        await PutAsync(client, csrf, name, 0, source);
        var ready = await UntilAsync(client, name, "ready");
        var previous = ready.GetProperty("preview").GetProperty("id").GetString();
        foreach (var exclude in new[] { "main.people_test", "" })
        {
            using var edited = await HttpJourney.PostFormAsync(client, $"/api/v1/dashboard/imports/{name}/options", new() { ["_csrf"] = csrf, ["exclude"] = exclude });
            await Assert.That(edited.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            var options = (await JsonAsync(edited)).GetProperty("options");
            await Assert.That(options.GetProperty("collection").GetString()).IsEqualTo("people_test");
            await Assert.That(options.GetProperty("exclude").GetArrayLength()).IsEqualTo(exclude.Length == 0 ? 0 : 1);
            ready = await UntilAsync(client, name, "ready");
            await Assert.That(ready.GetProperty("preview").GetProperty("id").GetString()).IsNotEqualTo(previous);
        }
        await Assert.That(ready.GetProperty("preview").GetProperty("tables")[0].GetProperty("collection").GetString()).IsEqualTo("people_test");
        await StartAsync(client, csrf, name, ready.GetProperty("preview").GetProperty("id").GetString()!, true);
        var complete = await UntilAsync(client, name, "completed");
        await Assert.That(complete.GetProperty("validation").GetProperty("passed").GetBoolean()).IsTrue();
        await Assert.That(complete.GetProperty("validation").GetProperty("tables")[0].GetProperty("documents").GetInt64()).IsEqualTo(2);
        var id = (await JsonAsync(await client.GetAsync("/api/v1/dashboard/databases"))).EnumerateArray().Single(item => item.GetProperty("name").GetString() == name).GetProperty("id").GetString();
        var documents = await JsonAsync(await client.GetAsync("/api/v1/dashboard/data/documents?databaseId=" + id + "&collection=people_test"));
        await Assert.That(documents.EnumerateArray().Select(row => row.GetProperty("value").GetProperty("name").GetString()).Order().SequenceEqual(new[] { "Ada", "Grace" })).IsTrue();
    }

    [Test]
    [Arguments("Bad name", "ndjson")]
    [Arguments("sqlite_users", "ndjson")]
    [Arguments("turso_users", "ndjson")]
    [Arguments("fts_dir__tb_fts_index_test", "ndjson")]
    [Arguments("collections", "ndjson")]
    [Arguments("commit_offset", "ndjson")]
    [Arguments("terrascale-import", "ndjson")]
    [Arguments("con", "ndjson")]
    [Arguments("people\n", "ndjson")]
    [Arguments("people", "sqlite")]
    public async Task InvalidOrReservedTargetCreatesNoDatabase(string collection, string format)
    {
        await using var scenario = await ImportScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var name = Minted(await HtmlAsync(client, "/imports"));
        var csrf = await HttpJourney.CsrfAsync(client, "/imports");
        var form = Form(csrf, name, format, Encoding.UTF8.GetBytes("{\"name\":\"Ada\"}\n"));
        form["collection"] = collection;
        using var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/imports", form);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await DatabaseCountAsync(client, name)).IsEqualTo(0);
        await Assert.That((await JsonAsync(await client.GetAsync("/__test/imports/requests"))).GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task NdjsonExportsAreOfferedAndAcceptedAsAnImportFormat()
    {
        await using var scenario = await ImportScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var page = await client.GetStringAsync("/imports");
        await Assert.That(page).Contains("value=\"ndjson\"");
        var csrf = await HttpJourney.CsrfAsync(client, "/imports");
        var job = await CreateAsync(client, csrf, Minted(page), "ndjson", Encoding.UTF8.GetBytes("{\"collection\":\"users\",\"email\":\"ada@example.com\"}\n"));
        await Assert.That(job.GetProperty("state").GetString()).IsEqualTo("awaiting_source");
        await Assert.That(job.GetProperty("source").GetProperty("format").GetString()).IsEqualTo("ndjson");
    }

    [Test]
    [Arguments("application/json", "application/problem+json")]
    [Arguments("text/html", "text/html")]
    public async Task CustomDatabaseNameIdentifiesTheFieldWithoutCreatingADestination(string accept, string contentType)
    {
        await using var scenario = await ImportScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/imports");
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);

        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/imports", Form(csrf, "custom", "postgres_plain", Shop(100)));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(contentType);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("The database field must use the destination name generated by this page. Custom database names are not supported. Reload the page to start a new import.");
        await Assert.That(await DatabaseCountAsync(client, "custom")).IsEqualTo(0);
        await Assert.That((await JsonAsync(await client.GetAsync("/__test/imports/requests"))).GetArrayLength()).IsEqualTo(0)
            .Because("An invalid database field must not create an import upstream.");
    }

    [Test]
    public async Task PostgresDumpImportsTheExactConfirmedPreviewIntoANewDatabase()
    {
        await using var scenario = await ImportScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        await HttpJourney.LoginAsync(client);
        await HttpJourney.LoginAsync(replica);
        var page = await HtmlAsync(client, "/imports");
        await Assert.That(page).Contains("Import from PostgreSQL, Turso or NDJSON");
        await Assert.That(page).Contains("A partial upload is discarded after 1 day without progress (the upload then starts over); an unconfirmed preview is kept 7 days, and a failed or cancelled import's file 7 days.");
        await Assert.That(page.Contains("type=\"password\"", StringComparison.Ordinal)).IsFalse().Because("The dashboard never collects source credentials.");
        await Assert.That((await JsonAsync(await client.GetAsync("/api/v1/dashboard/imports"))).GetArrayLength()).IsEqualTo(0);
        await Assert.That(page.Contains("missingPrimaryKey", StringComparison.Ordinal)).IsFalse();
        await Assert.That(page).Contains("Source primary keys stay document fields");
        var name = Minted(page);
        var csrf = await HttpJourney.CsrfAsync(client, "/imports");
        var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/imports");
        var source = Shop(Part + 1000);

        var job = await CreateAsync(client, csrf, name, "postgres_plain", source);
        await Assert.That(job.GetProperty("state").GetString()).IsEqualTo("awaiting_source");
        var replay = await CreateAsync(replica, replicaCsrf, name, "postgres_plain", source);
        await Assert.That(replay.GetProperty("id").GetString()).IsEqualTo(job.GetProperty("id").GetString()).Because("A resubmitted form reuses its destination and gets the identical import back.");
        await Assert.That(await DatabaseCountAsync(replica, name)).IsEqualTo(1);

        var (status, body) = await PutAsync(replica, replicaCsrf, name, 0, source[..Part]);
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("source").GetProperty("receivedBytes").GetInt64()).IsEqualTo(Part);
        (status, body) = await PutAsync(client, csrf, name, 0, source[..Part]);
        await Assert.That(body.GetProperty("source").GetProperty("receivedBytes").GetInt64()).IsEqualTo(Part).Because("A replayed part is a no-op.");
        (status, body) = await PutAsync(client, csrf, name, Part + 1, source[(Part + 1)..(Part + 2)]);
        await Assert.That(status).IsEqualTo(HttpStatusCode.Conflict).Because("A part that skips bytes is refused; one ending at or before receivedBytes would be a replay.");
        await Assert.That(body.GetProperty("detail").GetString()).Contains("continues where it stopped");
        // Expect: 100-continue lets the client read the dashboard's 413, sent from Content-Length alone, before any body byte.
        (status, body) = await PutAsync(client, csrf, name, Part, new byte[Part + 1], expectContinue: true);
        await Assert.That(status).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(body.GetProperty("detail").GetString()).Contains("1 byte to 4 MiB");
        var unchanged = await JsonAsync(await client.GetAsync("/api/v1/dashboard/imports/" + name));
        await Assert.That(unchanged.GetProperty("source").GetProperty("receivedBytes").GetInt64()).IsEqualTo(Part).Because("The same client reconnects and TerraBase holds only the first part.");
        (status, _) = await PutAsync(client, "", name, Part, source[Part..]);
        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        (status, body) = await PutAsync(client, csrf, name, Part, source[Part..]);
        await Assert.That(body.GetProperty("state").GetString()).IsEqualTo("analyzing");

        var analyzing = await HtmlAsync(replica, "/imports/" + name);
        await Assert.That(analyzing).Contains("Reading the export");
        await Assert.That(analyzing).Contains("data-on-interval__duration.2s");
        var rejected = await NextAsync(client, name);
        await Assert.That(rejected.GetProperty("state").GetString()).IsEqualTo("rejected");
        await Assert.That(rejected.GetProperty("preview").GetProperty("blocking").GetInt32()).IsEqualTo(1);
        var blocked = await HtmlAsync(client, "/imports/" + name);
        await Assert.That(blocked).Contains("1 blocking issue");
        await Assert.That(blocked).Contains("text[] has no mapping; exclude the column");
        await Assert.That(blocked.Contains("/start\"", StringComparison.Ordinal)).IsFalse().Because("A blocked preview cannot be started.");

        using (var options = await HttpJourney.PostFormAsync(client, $"/api/v1/dashboard/imports/{name}/options", new()
        { ["_csrf"] = csrf, ["exclude"] = "public.orders.tags" }))
            await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var ready = await NextAsync(replica, name);
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready");
        await Assert.That(ready.GetProperty("options").TryGetProperty("missingPrimaryKey", out _)).IsFalse();
        await Assert.That(ready.GetProperty("preview").GetProperty("tables")[0].TryGetProperty("idPolicy", out _)).IsFalse();
        var previewId = ready.GetProperty("preview").GetProperty("id").GetString()!;
        var confirm = await HtmlAsync(replica, "/imports/" + name);
        foreach (var disclosure in new[] { "Confirm this preview", previewId, "public.customers.balance: numeric(12,2) is stored as a string with the exact digits",
            "foreign key orders_customer_fk (customer_id → public.customers(id)) is recorded but not enforced", "public.orders.tags is not imported.",
            "public.audit_log: every row gets a deterministic generated ULID; source primary keys remain document fields.", "public.touch()", "Do not write to " + name })
            await Assert.That(confirm).Contains(disclosure);

        await Assert.That((await StartAsync(client, csrf, name, "01STALEPREVIEW", true)).Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await StartAsync(client, csrf, name, previewId, false)).Status).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        var started = await StartAsync(client, csrf, name, previewId, true);
        await Assert.That(started.Status).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(started.Body.GetProperty("confirmedPreviewId").GetString()).IsEqualTo(previewId);
        await Assert.That((await StartAsync(replica, replicaCsrf, name, previewId, true)).Body.GetProperty("state").GetString()).IsEqualTo("queued");

        var completed = await UntilAsync(client, name, "completed");
        await Assert.That(completed.GetProperty("validation").GetProperty("passed").GetBoolean()).IsTrue();
        await Assert.That(completed.GetProperty("validation").GetProperty("tables").GetArrayLength()).IsEqualTo(3);
        await Assert.That(await HtmlAsync(replica, "/imports/" + name)).Contains("Import verified");
        await Assert.That((await JsonAsync(await replica.GetAsync("/api/v1/dashboard/imports"))).EnumerateArray().Single().GetProperty("database").GetString()).IsEqualTo(name);

        // The Free plan's one database is the destination: createDatabase refuses another before any import call.
        var second = Minted(await HtmlAsync(client, "/imports"));
        using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/imports", Form(csrf, second, "postgres_plain", source)))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That((await JsonAsync(refused)).GetProperty("detail").GetString()).Contains("database limit");
        }

        var requests = (await JsonAsync(await client.GetAsync("/__test/imports/requests"))).EnumerateArray().ToArray();
        await Assert.That(requests.All(item => item.GetProperty("bearer").GetString() == "test-user" && item.GetProperty("host").GetString() == "gru.api.terrascale.tech" &&
            item.GetProperty("pin").GetString() == "gru")).IsTrue()
            .Because("Every import call carries the user's exchanged project token to the destination's home region, pinned there.");
        await Assert.That(requests.Where(item => item.GetProperty("route").GetString() == "import" && item.GetProperty("method").GetString() == "POST")
            .All(item => item.GetProperty("fields").EnumerateArray().Select(field => field.GetString()).Order().SequenceEqual(["format", "options", "sourceBytes", "sourceSha256"]))).IsTrue()
            .Because("createImport carries only the file fingerprint and options: no credentials or source references.");
        await Assert.That(requests.Count(item => item.GetProperty("route").GetString() == "source")).IsEqualTo(4).Because("Oversized and unauthenticated parts never leave the dashboard.");
        await Assert.That(requests.Max(item => item.GetProperty("bytes").GetInt64())).IsLessThanOrEqualTo(Part);
    }

    [Test]
    public async Task TursoExportRecoversFromRefusalTransferFaultFailureCancellationAndLostSource()
    {
        await using var scenario = await ImportScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        await HttpJourney.LoginAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client, "/imports");

        var wal = Sqlite(journal: 2, 4096);
        var refused = Minted(await HtmlAsync(client, "/imports"));
        await CreateAsync(client, csrf, refused, "sqlite", wal);
        await PutAsync(client, csrf, refused, 0, wal);
        var rejected = await NextAsync(client, refused);
        await Assert.That(rejected.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("wal_mode_source");
        await Assert.That(await HtmlAsync(client, "/imports/" + refused)).Contains("Make a rollback-mode copy with VACUUM INTO.");
        var databases = await JsonAsync(await client.GetAsync("/api/v1/dashboard/databases"));
        var refusedId = databases.EnumerateArray().Single(item => item.GetProperty("name").GetString() == refused).GetProperty("id").GetString()!;
        using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new() { ["_csrf"] = csrf, ["databaseId"] = refusedId, ["confirmName"] = refused }))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because("Deleting the destination is the cleanup for an unusable export.");
        await Assert.That((await client.GetAsync("/api/v1/dashboard/imports/" + refused)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        var source = Sqlite(journal: 1, 5000);
        var name = Minted(await HtmlAsync(client, "/imports"));
        await CreateAsync(client, csrf, name, "sqlite", source);
        await ControlAsync(client, name, new { command = "corrupt" });
        var (_, damaged) = await PutAsync(client, csrf, name, 0, source);
        await Assert.That(damaged.GetProperty("state").GetString()).IsEqualTo("awaiting_source");
        await Assert.That(damaged.GetProperty("source").GetProperty("receivedBytes").GetInt64()).IsEqualTo(0);
        await Assert.That(await HtmlAsync(client, "/imports/" + name)).Contains("The upload was damaged in transit");
        await Assert.That((await NextAsync(client, name)).GetProperty("state").GetString()).IsEqualTo("awaiting_source").Because("Only the user re-sends a damaged source.");

        await PutAsync(client, csrf, name, 0, source);
        var ready = await NextAsync(client, name);
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready");
        var preview = await HtmlAsync(client, "/imports/" + name);
        foreach (var disclosure in new[] { "main.notes.score: NUMERIC is stored as a value whose JSON type differs between rows (integer, string)",
            "main.events: every row gets a deterministic generated ULID; source primary keys remain document fields.", "source primary key fields: note_id, tag", "main.tags: foreign key (unnamed) (note_id → main.notes(id)) is recorded but not enforced.", "main.notes_fts" })
            await Assert.That(preview).Contains(disclosure);
        var previewId = ready.GetProperty("preview").GetProperty("id").GetString()!;
        await StartAsync(client, csrf, name, previewId, true);
        await Assert.That((await NextAsync(client, name)).GetProperty("state").GetString()).IsEqualTo("running");

        await ControlAsync(client, name, new { command = "fail", code = "write_failed", retryable = true });
        var failed = await NextAsync(client, name);
        await Assert.That(failed.GetProperty("state").GetString()).IsEqualTo("failed");
        var stopped = await HtmlAsync(client, "/imports/" + name);
        await Assert.That(stopped).Contains("write_failed · main.notes · row 1");
        await Assert.That(stopped).Contains("Resuming continues copying after the last committed batch");
        var resumed = await PostAsync(client, csrf, name, "resume");
        await Assert.That(resumed.GetProperty("state").GetString()).IsEqualTo("queued");
        await Assert.That(resumed.GetProperty("confirmedPreviewId").GetString()).IsEqualTo(previewId);
        await NextAsync(client, name);

        await Assert.That((await PostAsync(client, csrf, name, "cancel")).GetProperty("cancelRequested").GetBoolean()).IsTrue();
        await Assert.That((await NextAsync(client, name)).GetProperty("state").GetString()).IsEqualTo("cancelled");
        await Assert.That(await HtmlAsync(client, "/imports/" + name)).Contains("TerraBase keeps the uploaded file until");

        await ControlAsync(client, name, new { command = "lose-source" });
        using (var required = await HttpJourney.PostFormAsync(client, $"/api/v1/dashboard/imports/{name}/resume", new() { ["_csrf"] = csrf }))
        {
            await Assert.That(required.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That((await JsonAsync(required)).GetProperty("detail").GetString()).Contains("no longer holds the export");
        }
        await Assert.That(await HtmlAsync(client, "/imports/" + name)).Contains("Upload the export again to resume");
        await PutAsync(client, csrf, name, 0, source);
        await PostAsync(client, csrf, name, "resume");
        var completed = await UntilAsync(client, name, "completed");
        await Assert.That(completed.GetProperty("validation").GetProperty("passed").GetBoolean()).IsTrue();
    }

    // ts-auth grants import authority by role on every exchange: the owner (admin) and a member write, a viewer only
    // reads and a nonmember reaches nothing. A refused part is answered before the dashboard reads a byte of its body.
    [Test]
    public async Task ImportAuthorityFollowsTsAuthRolesAndRefusedPartsSendNoBytes()
    {
        await using var scenario = await ImportScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        await HttpJourney.LoginAsync(client);
        var name = Minted(await HtmlAsync(client, "/imports"));
        var csrf = await HttpJourney.CsrfAsync(client, "/imports");
        // The second part is above 1 KiB: .NET sends a body of at most 1 KiB even after an error response.
        var source = Shop(Part + 4096);
        await CreateAsync(client, csrf, name, "postgres_plain", source);
        var (status, body) = await PutAsync(client, csrf, name, 0, source[..Part]);
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var other = Minted(await HtmlAsync(client, "/imports"));

        scenario.Workspace.Role = "viewer";
        var read = await JsonAsync(await client.GetAsync("/api/v1/dashboard/imports/" + name));
        await Assert.That(read.GetProperty("source").GetProperty("receivedBytes").GetInt64()).IsEqualTo(Part).Because("A viewer reads the import.");
        var (refused, detail, sent) = await RefusedPutAsync(client, csrf, name, Part, source[Part..]);
        await Assert.That(refused).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(detail.GetProperty("detail").GetString()).IsEqualTo("Your project role does not allow this import action.");
        await Assert.That(sent).IsEqualTo(0L).Because("The viewer's part is refused before the dashboard reads its body.");
        using (var cancel = await HttpJourney.PostFormAsync(client, $"/api/v1/dashboard/imports/{name}/cancel", new() { ["_csrf"] = csrf }))
            await Assert.That(cancel.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await HtmlAsync(client, "/imports")).DoesNotContain("name=\"database\"").Because("A viewer is offered no import form.");
        using (var create = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/imports", Form(csrf, other, "postgres_plain", source)))
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await DatabaseCountAsync(client, other)).IsEqualTo(0).Because("A viewer creates no destination.");

        scenario.Workspace.Role = "";
        (refused, _, sent) = await RefusedPutAsync(client, csrf, name, Part, source[Part..]);
        await Assert.That(refused).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(sent).IsEqualTo(0L).Because("A nonmember's part is refused before the dashboard reads its body.");

        scenario.Workspace.Role = "member";
        await Assert.That(await HtmlAsync(client, "/imports/" + name)).Contains("Upload the export").Because("A member writes imports.");
        (status, body) = await PutAsync(client, csrf, name, Part, source[Part..]);
        await Assert.That(body.GetProperty("state").GetString()).IsEqualTo("analyzing");
        var requests = (await JsonAsync(await client.GetAsync("/__test/imports/requests"))).EnumerateArray().ToArray();
        await Assert.That(requests.Count(item => item.GetProperty("route").GetString() == "source")).IsEqualTo(2).Because("Only the writer's two parts reach TerraBase.");
        await Assert.That(requests.All(item => item.GetProperty("scopes").GetString() == (item.GetProperty("method").GetString() == "GET" ? "databases:read" : "databases:write documents:write")))
            .IsTrue().Because("Each import request carries exactly its route's scopes.");
    }

    private static byte[] Shop(int size)
    {
        var bytes = new byte[size];
        Encoding.UTF8.GetBytes("-- TerraScale import fixture: shop\n").CopyTo(bytes, 0);
        return bytes;
    }

    // A synthetic SQLite header; bytes 18 and 19 are the write/read format versions (1 rollback journal, 2 WAL).
    private static byte[] Sqlite(byte journal, int size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
        bytes[18] = journal;
        bytes[19] = journal;
        return bytes;
    }

    private static Dictionary<string, string> Form(string csrf, string name, string format, byte[] source) => new()
    {
        ["_csrf"] = csrf,
        ["database"] = name,
        ["format"] = format,
        ["sourceBytes"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["sourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(source))
    };

    private static async Task<JsonElement> CreateAsync(HttpClient client, string csrf, string name, string format, byte[] source)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/imports", Form(csrf, name, format, source));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await ControlAsync(client, name, new { command = "manual" });
        return await JsonAsync(response);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(HttpClient client, string csrf, string name, long offset, byte[] part, bool expectContinue = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/dashboard/imports/{name}/source?offset={offset}") { Content = new ByteArrayContent(part) };
        request.Headers.ExpectContinue = expectContinue;
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        if (csrf.Length > 0) request.Headers.Add("X-CSRF-Token", csrf);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await JsonAsync(response));
    }

    // Expect: 100-continue holds the body until the dashboard reads it, so Sent counts the bytes it asked for.
    private static async Task<(HttpStatusCode Status, JsonElement Body, long Sent)> RefusedPutAsync(HttpClient client, string csrf, string name, long offset, byte[] part)
    {
        using var stream = new MemoryStream(part);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/dashboard/imports/{name}/source?offset={offset}") { Content = new StreamContent(stream) };
        request.Headers.ExpectContinue = true;
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Content.Headers.ContentLength = part.Length;
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-CSRF-Token", csrf);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await JsonAsync(response), stream.Position);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> StartAsync(HttpClient client, string csrf, string name, string previewId, bool confirm)
    {
        var form = new Dictionary<string, string> { ["_csrf"] = csrf, ["previewId"] = previewId };
        if (confirm) form["confirm"] = "1";
        using var response = await HttpJourney.PostFormAsync(client, $"/api/v1/dashboard/imports/{name}/start", form);
        return (response.StatusCode, await JsonAsync(response));
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string csrf, string name, string action)
    {
        using var response = await HttpJourney.PostFormAsync(client, $"/api/v1/dashboard/imports/{name}/{action}", new() { ["_csrf"] = csrf });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        return await JsonAsync(response);
    }

    private static async Task ControlAsync(HttpClient client, string name, object command)
    {
        var body = JsonSerializer.SerializeToNode(command)!.AsObject();
        body["database"] = name;
        using var response = await client.PostAsJsonAsync("/__test/imports/control", body);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    // One producer step, then the dashboard's read of the job, as a poll would observe it.
    private static async Task<JsonElement> NextAsync(HttpClient client, string name)
    {
        await ControlAsync(client, name, new { command = "step" });
        return await JsonAsync(await client.GetAsync("/api/v1/dashboard/imports/" + name));
    }

    private static async Task<JsonElement> UntilAsync(HttpClient client, string name, string state)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var job = await NextAsync(client, name);
            if (job.GetProperty("state").GetString() == state) return job;
        }
        throw new InvalidOperationException("The import did not reach " + state + ".");
    }

    private static async Task<int> DatabaseCountAsync(HttpClient client, string name) =>
        (await JsonAsync(await client.GetAsync("/api/v1/dashboard/databases"))).EnumerateArray().Count(item => item.GetProperty("name").GetString() == name);

    private static string Minted(string page) => Regex.Match(page, "name=\"database\" value=\"(import-[0-9a-z]{26})\"").Groups[1].Value is { Length: > 0 } name
        ? name : throw new InvalidOperationException("The import page did not mint a destination name.");

    private static async Task<string> HtmlAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
