using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Import;
using TerraBase.Features.Management.Usage;
using TerraBase.Testing;

namespace TerraBase.Fast.Import;

/// <summary>
/// The import contract (docs/terrabase/import.md) against the real server: genuine pg_dump 17.11 and sqlite3 3.51.2 fixtures
/// (scripts/terrabase/import-fixtures.sh), the guided flow, the safety refusals and the state machine.
/// </summary>
public sealed class ImportApiTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Import", "Fixtures");
    private static readonly string[] TaggedArray = ["public.tagged.tags"];

    [Test]
    public async Task A_genuine_pg_dump_previews_blocking_issues_then_imports_every_row_after_new_options()
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var dump = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "pg17-shop.sql"));

        var (created, job) = await client.CreateAsync(import, "postgres_plain", dump);
        await Assert.That(created).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(job.GetProperty("state").GetString()).IsEqualTo("awaiting_source");
        var (uploaded, _) = await client.UploadAsync(import, dump, part: 1000);
        await Assert.That(uploaded).IsEqualTo(HttpStatusCode.OK);

        // Dry run: tagged holds an array, so the default options block; events has no primary key and imports with generated ids.
        var rejected = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(rejected.GetProperty("state").GetString()).IsEqualTo("rejected").Because(rejected.GetRawText());
        var tables = Tables(rejected);
        await Assert.That(tables["public.events"].GetProperty("issues").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Issue(tables["public.tagged"], "unsupported_type")).IsTrue();
        await Assert.That(tables.ContainsKey("public.measurements")).IsFalse();
        var skipped = rejected.GetProperty("preview").GetProperty("skipped").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString() + " " + entry.GetProperty("name").GetString()).ToList();
        await Assert.That(skipped).Contains("partitioned_table public.measurements");
        await Assert.That(skipped).Contains("function public.touch");
        await Assert.That(skipped).Contains("view public.active_users");
        await Assert.That(skipped).Contains("index users_org_idx");
        await Assert.That(skipped).Contains("check_constraint public.users.users_score_check");
        // Nothing is written by a dry run.
        await Assert.That(await client.IdsAsync(import, "users")).IsEmpty();

        var stale = rejected.GetProperty("preview").GetProperty("id").GetString();
        var (updated, _) = await client.UpdateAsync(import, new { exclude = TaggedArray });
        await Assert.That(updated).IsEqualTo(HttpStatusCode.Accepted);
        var ready = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready").Because(ready.GetRawText());
        var preview = ready.GetProperty("preview");
        await Assert.That(preview.GetProperty("blocking").GetInt32()).IsEqualTo(0);
        await Assert.That(preview.GetProperty("id").GetString()).IsNotEqualTo(stale);
        tables = Tables(ready);
        var users = tables["public.users"];
        await Assert.That(Mapping(users, "balance")).IsEqualTo("decimal_string");
        await Assert.That(Mapping(users, "mood")).IsEqualTo("string");
        await Assert.That(Mapping(users, "created_at")).IsEqualTo("timestamptz");
        await Assert.That(Mapping(users, "flags")).IsEqualTo("text");
        await Assert.That(users.GetProperty("constraints").EnumerateArray().Any(constraint =>
            constraint.GetProperty("kind").GetString() == "foreign_key" && constraint.GetProperty("references").GetString() == "public.orgs(id)"
            && !constraint.GetProperty("enforced").GetBoolean())).IsTrue();
        await Assert.That(tables["sales.\"Order Items\""].GetProperty("collection").GetString()).IsEqualTo("sales_order_items");

        // A stale preview id cannot start the copy.
        var (refused, refusal) = await client.StartAsync(import, stale);
        await Assert.That(refused).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(refusal.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("preview_changed");

        var (started, _) = await client.StartAsync(import, preview.GetProperty("id").GetString());
        await Assert.That(started).IsEqualTo(HttpStatusCode.Accepted);
        var done = await client.WaitAsync(import, "completed", "failed");
        await Assert.That(done.GetProperty("state").GetString()).IsEqualTo("completed").Because(done.GetRawText());
        var validation = done.GetProperty("validation");
        await Assert.That(validation.GetProperty("passed").GetBoolean()).IsTrue();
        await Assert.That(validation.GetProperty("tables").EnumerateArray().All(table => table.GetProperty("match").GetBoolean())).IsTrue();
        await Assert.That(done.GetProperty("progress").GetProperty("rowsWritten").GetInt64()).IsEqualTo(15);
        await Assert.That(done.GetProperty("source").GetProperty("staged").GetBoolean()).IsFalse();

        var ada = await client.DocumentAsync(import, "users", ("id", "5f0c8e9a-6a43-4c3e-9a7d-2d1f0c5e8b11"));
        await Assert.That(ada.GetProperty("balance").GetString()).IsEqualTo("1234567890.1234");
        await Assert.That(ada.GetProperty("score").GetRawText()).IsEqualTo("0.1");
        await Assert.That(ada.GetProperty("small").GetInt32()).IsEqualTo(-32768);
        await Assert.That(ada.GetProperty("active").GetBoolean()).IsTrue();
        await Assert.That(ada.GetProperty("mood").GetString()).IsEqualTo("happy");
        await Assert.That(ada.GetProperty("born").GetString()).IsEqualTo("1815-12-10");
        await Assert.That(ada.GetProperty("seen_at").GetString()).IsEqualTo("2026-10-12T08:30:00.123456");
        await Assert.That(ada.GetProperty("created_at").GetString()).IsEqualTo("2026-10-12T08:30:00.123456Z");
        await Assert.That(ada.GetProperty("profile").GetProperty("ü").GetString()).IsEqualTo("ß");
        await Assert.That(ada.GetProperty("avatar").GetString()).IsEqualTo("AP8Q");
        await Assert.That(ada.GetProperty("span").GetString()).IsEqualTo("1 year 2 mons 3 days 04:05:06");
        await Assert.That(ada.GetProperty("Display Name").GetString()).IsEqualTo("Ada Lovelace");
        var odd = await client.DocumentAsync(import, "users", ("id", "a1b2c3d4-e5f6-4711-8899-aabbccddeeff"));
        await Assert.That(odd.GetProperty("Display Name").GetString()).IsEqualTo("Quote \" and \\ backslash");
        await Assert.That(odd.GetProperty("created_at").GetString()).IsEqualTo("2000-02-29T06:30:00Z");
        await Assert.That(odd.GetProperty("avatar").GetString()).IsEqualTo("");
        await Assert.That((await client.DocumentAsync(import, "orgs", ("id", "9007199254740993"))).GetProperty("id").GetRawText()).IsEqualTo("9007199254740993");
        await Assert.That((await client.DocumentAsync(import, "orgs", ("id", "2"))).GetProperty("name").GetString()).IsEqualTo("Tab\tand\nnewline");
        var orderItems = await client.IdsAsync(import, "sales_order_items");
        await Assert.That(orderItems.Count(ImportClient.IsUlid)).IsEqualTo(3);
        await Assert.That((await client.KeysAsync(import, "sales_order_items", "order_id", "line")).Order(StringComparer.Ordinal)).IsEquivalentTo(["1:1", "1:2", "2:1"]);
        await Assert.That((await client.DocumentAsync(import, "sales_order_items", ("order_id", "1"), ("line", "2"))).GetProperty("unit.price").GetString()).IsEqualTo("0.000000000000000000000000001");
        await Assert.That((await client.DocumentAsync(import, "tagged", ("id", "1"))).TryGetProperty("tags", out _)).IsFalse();
        await Assert.That((await client.KeysAsync(import, "measurements_2026", "id", "taken")).Order(StringComparer.Ordinal)).IsEquivalentTo(["2:2026-06-01", "3:2026-07-01"]);
        var events = await client.IdsAsync(import, "events");
        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events.All(ImportClient.IsUlid)).IsTrue();
    }

    [Test]
    public async Task A_genuine_sqlite_file_imports_through_the_managed_reader_and_skips_virtual_and_internal_tables()
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var file = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "sqlite-notes.db"));
        _ = await client.CreateAsync(import, "sqlite", file);
        _ = await client.UploadAsync(import, file);
        var ready = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready").Because(ready.GetRawText());
        var skipped = ready.GetProperty("preview").GetProperty("skipped").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString() + " " + entry.GetProperty("name").GetString()).ToList();
        await Assert.That(skipped).Contains("virtual_table main.notes_fts");
        await Assert.That(skipped).Contains("shadow_table main.notes_fts_data");
        await Assert.That(skipped).Contains("internal_table main.sqlite_sequence");
        await Assert.That(skipped).Contains("internal_table main.sqlite_stat1");
        await Assert.That(skipped).Contains("view main.titles");
        var tables = Tables(ready);
        await Assert.That(tables.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(["main.\"Weird.Name\"", "main.g", "main.kv", "main.notes", "main.plain", "main.wr"]);
        await Assert.That(tables["main.notes"].GetProperty("rows").GetInt64()).IsEqualTo(1203);
        await Assert.That(Mapping(tables["main.notes"], "misc")).IsEqualTo("integer+string");
        await Assert.That(Issue(tables["main.notes"], "mixed_storage_classes")).IsTrue();
        await Assert.That(Mapping(tables["main.notes"], "data")).IsEqualTo("base64");
        // plain has neither a primary key nor a declared type: it imports like any table, without an issue.
        await Assert.That(tables["main.plain"].GetProperty("issues").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Issue(tables["main.g"], "not_imported")).IsTrue();

        _ = await client.StartAsync(import, ready.GetProperty("preview").GetProperty("id").GetString());
        var done = await client.WaitAsync(import, "completed", "failed");
        await Assert.That(done.GetProperty("state").GetString()).IsEqualTo("completed").Because(done.GetRawText());
        await Assert.That(done.GetProperty("validation").GetProperty("passed").GetBoolean()).IsTrue();

        var notes = await client.IdsAsync(import, "notes");
        await Assert.That(notes.Count(ImportClient.IsUlid)).IsEqualTo(1203);
        await Assert.That(notes.Distinct().Count()).IsEqualTo(1203);
        var unicode = await client.DocumentAsync(import, "notes", ("id", "3"));
        await Assert.That(unicode.GetProperty("body").GetString()).IsEqualTo("ü ß 漢字 🎉");
        await Assert.That(unicode.GetProperty("id").GetInt64()).IsEqualTo(3);
        await Assert.That((await client.DocumentAsync(import, "notes", ("id", "1"))).GetProperty("data").GetString()).IsEqualTo("AP8=");
        // Rows that spill into overflow pages come back whole.
        await Assert.That((await client.DocumentAsync(import, "notes", ("id", "303"))).GetProperty("body").GetString()!.Length).IsEqualTo(9000);
        // Rows written before ALTER TABLE … ADD COLUMN get the column's literal default.
        await Assert.That((await client.DocumentAsync(import, "plain", ("x", "1"))).GetProperty("z").GetString()).IsEqualTo("zed");
        await Assert.That((await client.DocumentAsync(import, "plain", ("x", "5"))).GetProperty("z").GetString()).IsEqualTo("set");
        // WITHOUT ROWID records hold the key (c, a) first; every key column still lands in its own field.
        await Assert.That((await client.KeysAsync(import, "wr", "c", "a")).Order(StringComparer.Ordinal)).IsEquivalentTo(["x:1", "x:2", "y:1"]);
        await Assert.That((await client.DocumentAsync(import, "wr", ("c", "x"), ("a", "2"))).GetProperty("b").GetString()).IsEqualTo("bee2");
        await Assert.That((await client.KeysAsync(import, "kv", "k")).Order(StringComparer.Ordinal)).IsEquivalentTo(["a", "b:c", "d%e"]);
        var generated = await client.DocumentAsync(import, "g", ("a", "2"));
        await Assert.That(generated.GetProperty("c").GetInt32()).IsEqualTo(3);
        await Assert.That(generated.GetProperty("d").GetString()).IsEqualTo("two");
        await Assert.That(generated.TryGetProperty("b", out _)).IsFalse();
        await Assert.That((await client.DocumentAsync(import, "weird_name", ("Col One", "10"))).GetProperty("two").GetString()).IsEqualTo("ten");
    }

    [Test]
    [Arguments("sqlite", "sqlite-wal.db", "wal_mode_source")]
    [Arguments("postgres_plain", "pg17-custom.dump", "unsupported_source_format")]
    [Arguments("postgres_plain", "pg17-inserts.sql", "unsupported_statement")]
    [Arguments("postgres_plain", "pg17-edge.sql", "invalid_value")]
    [Arguments("sqlite", "pg17-shop.sql", "unsupported_source_format")]
    public async Task Unsafe_or_unsupported_sources_are_rejected_before_anything_is_written(string format, string fixture, string code)
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Fixtures, fixture));
        _ = await client.CreateAsync(import, format, bytes);
        _ = await client.UploadAsync(import, bytes);
        var rejected = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(rejected.GetProperty("state").GetString()).IsEqualTo("rejected").Because(rejected.GetRawText());
        await Assert.That(rejected.GetProperty("error").ValueKind).IsEqualTo(JsonValueKind.Object).Because(rejected.GetRawText());
        await Assert.That(rejected.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        var (start, _) = await client.StartAsync(import, rejected.GetProperty("preview").ValueKind == JsonValueKind.Object
            ? rejected.GetProperty("preview").GetProperty("id").GetString() : "01ARZ3NDEKTSV4RRFFQ69G5FAV");
        await Assert.That(start).IsEqualTo(HttpStatusCode.Conflict);
        if (fixture == "pg17-edge.sql")
        {
            // Every value without a JSON form is reported with its row before any write.
            var issues = Tables(rejected)["public.readings"].GetProperty("issues").EnumerateArray()
                .Select(issue => (issue.GetProperty("row").GetInt64(), issue.GetProperty("column").GetString()!)).ToList();
            await Assert.That(issues).IsEquivalentTo([(2L, "at"), (3L, "day"), (4L, "value"), (5L, "value")]);
        }
    }

    [Test]
    public async Task Malformed_and_hostile_bytes_end_in_rejected_and_the_node_keeps_serving()
    {
        await using var client = await ImportClient.StartAsync();
        var valid = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "sqlite-notes.db"));
        var faker = new Faker { Random = new Randomizer(20261012) };
        // Random corruption may still leave a readable file; it must never crash the runner or fail the job.
        foreach (var seed in Enumerable.Range(0, 3))
        {
            var corrupt = Corrupt(valid, faker, 64);
            var random = await client.CreateDestinationAsync();
            _ = await client.CreateAsync(random, "sqlite", corrupt);
            _ = await client.UploadAsync(random, corrupt);
            await Assert.That((await client.WaitAsync(random, "ready", "rejected", "failed")).GetProperty("state").GetString()).IsNotEqualTo("failed").Because($"seed {seed}");
        }

        var cases = new List<(string Format, byte[] Bytes)>
        {
            ("sqlite", valid[..4096]),
            ("sqlite", Cycle(valid)),
            ("sqlite", Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(faker.Random.Bytes(4080)).ToArray()),
            ("postgres_plain", Encoding.UTF8.GetBytes("CREATE TABLE public.t (id integer NOT NULL);\nCOPY public.t (id) FROM stdin;\n1\n")),
            ("postgres_plain", Encoding.UTF8.GetBytes("CREATE TABLE public.t (id integer NOT NULL, b text);\nCOPY public.t (id, b) FROM stdin;\n1\n\\.\n")),
            ("postgres_plain", Encoding.UTF8.GetBytes("DO $$ BEGIN PERFORM pg_sleep(1); END $$;\n")),
            ("postgres_plain", [0xff, 0xfe, 0x00, 0x41]),
            ("postgres_plain", Encoding.UTF8.GetBytes("\\connect other\nCREATE TABLE public.t (id integer);\n")),
            ("postgres_plain", Encoding.UTF8.GetBytes("SELECT pg_catalog.lo_create('16401');\n")),
        };
        foreach (var (format, bytes) in cases)
        {
            var import = await client.CreateDestinationAsync();
            _ = await client.CreateAsync(import, format, bytes);
            _ = await client.UploadAsync(import, bytes);
            var job = await client.WaitAsync(import, "ready", "rejected", "failed");
            await Assert.That(job.GetProperty("state").GetString()).IsEqualTo("rejected").Because($"{format} case of {bytes.Length} bytes");
            await Assert.That(await client.IdsAsync(import, "terrascale-import")).IsEquivalentTo([ImportNames.JobId, ImportNames.StateId]);
        }

        // The runner still serves a good import afterwards.
        var good = await client.CreateDestinationAsync();
        _ = await client.CreateAsync(good, "sqlite", valid);
        _ = await client.UploadAsync(good, valid);
        await Assert.That((await client.WaitAsync(good, "ready", "rejected")).GetProperty("state").GetString()).IsEqualTo("ready");
    }

    [Test]
    public async Task The_protocol_refuses_populated_destinations_mismatched_replays_offsets_and_checksums()
    {
        await using var client = await ImportClient.StartAsync();
        var dump = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "pg17-shop.sql"));

        var populated = await client.CreateDestinationAsync();
        await CreateCollectionAsync(client, populated, "people");
        var (notEmpty, refusal) = await client.CreateAsync(populated, "postgres_plain", dump);
        await Assert.That(notEmpty).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(Code(refusal)).IsEqualTo("destination_not_empty");

        var import = await client.CreateDestinationAsync();
        var (first, job) = await client.CreateAsync(import, "postgres_plain", dump);
        var (replay, same) = await client.CreateAsync(import, "postgres_plain", dump);
        await Assert.That((first, replay)).IsEqualTo((HttpStatusCode.Created, HttpStatusCode.OK));
        await Assert.That(same.GetProperty("id").GetString()).IsEqualTo(job.GetProperty("id").GetString());
        var (different, other) = await client.CreateAsync(import, "postgres_plain", dump, new { exclude = TaggedArray });
        await Assert.That(different).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(Code(other)).IsEqualTo("import_exists");

        var (part, afterPart) = await client.PartAsync(import, 0, dump[..1000]);
        await Assert.That(part).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(afterPart.GetProperty("source").GetProperty("receivedBytes").GetInt64()).IsEqualTo(1000);
        var (again, _) = await client.PartAsync(import, 0, dump[..1000]);
        await Assert.That(again).IsEqualTo(HttpStatusCode.OK);
        var (gap, gapBody) = await client.PartAsync(import, 2000, dump[2000..3000]);
        await Assert.That(gap).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(Code(gapBody)).IsEqualTo("offset_mismatch");

        // A transfer fault: the bytes do not hash to sourceSha256, so they are discarded and the same file is uploaded again.
        var damaged = (byte[])dump.Clone();
        damaged[^10] ^= 0x20;
        _ = await client.UploadAsync(import, damaged, part: 1000, offset: 1000);
        await TerraBaseTestServer.EventuallyAsync(async token => (await client.GetAsync(import, token)).GetProperty("error").ValueKind == JsonValueKind.Object);
        var waiting = await client.GetAsync(import);
        await Assert.That(waiting.GetProperty("state").GetString()).IsEqualTo("awaiting_source");
        await Assert.That(waiting.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("source_checksum_mismatch");
        await Assert.That(waiting.GetProperty("source").GetProperty("receivedBytes").GetInt64()).IsEqualTo(0);
        _ = await client.UploadAsync(import, dump, part: 3000);
        await Assert.That((await client.WaitAsync(import, "ready", "rejected")).GetProperty("state").GetString()).IsEqualTo("rejected");

        // Only project tokens with the route's scopes reach imports.
        using var reader = client.Api.WithToken(ImportClient.Token(client.Api, client.Project, "databases:read documents:read"));
        using var forbidden = await reader.PostAsync(new Uri(import + "/cancel", UriKind.Relative), null);
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var stranger = client.Api.WithToken(ImportClient.Token(client.Api, Primitives.EntityIds.New().ToString(), ImportClient.AllScopes));
        using var elsewhere = await stranger.GetAsync(new Uri(import, UriKind.Relative));
        await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Cancel_before_start_never_resumes_into_copying_and_a_resume_needs_a_new_confirmation()
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var file = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "sqlite-notes.db"));
        _ = await client.CreateAsync(import, "sqlite", file);
        _ = await client.UploadAsync(import, file);
        var ready = await client.WaitAsync(import, "ready");
        var confirmedBefore = ready.GetProperty("preview").GetProperty("id").GetString();

        var (cancelled, job) = await client.CancelAsync(import);
        await Assert.That(cancelled).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(job.GetProperty("state").GetString()).IsEqualTo("cancelled");
        await Assert.That(job.GetProperty("source").GetProperty("retainedUntil").ValueKind).IsEqualTo(JsonValueKind.String);
        var (stale, refusal) = await client.StartAsync(import, confirmedBefore);
        await Assert.That(stale).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(Code(refusal)).IsEqualTo("invalid_state");

        var (resumed, analyzing) = await client.ResumeAsync(import);
        await Assert.That(resumed).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(analyzing.GetProperty("state").GetString()).IsEqualTo("analyzing");
        var again = await client.WaitAsync(import, "ready");
        await Assert.That(again.GetProperty("preview").GetProperty("id").GetString()).IsNotEqualTo(confirmedBefore);
        await Assert.That(again.GetProperty("confirmedPreviewId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(await client.IdsAsync(import, "notes")).IsEmpty();
    }

    [Test]
    public async Task A_concurrent_ordinary_write_is_never_overwritten_and_the_copy_resumes_from_its_checkpoint()
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var file = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "sqlite-notes.db"));
        _ = await client.CreateAsync(import, "sqlite", file);
        _ = await client.UploadAsync(import, file);
        var ready = await client.WaitAsync(import, "ready");

        // Someone writes into the destination between preview and start, at the id the import derives for row 700 of notes.
        var faker = new Faker();
        var title = faker.Lorem.Sentence();
        var taken = TerraBase.Features.Import.ImportMapping.GeneratedId(ready.GetProperty("id").GetString()!, ready.GetProperty("createdAt").GetDateTimeOffset(), "main.notes", 700);
        await CreateCollectionAsync(client, import, "notes");
        using (var put = await client.Client.PutAsJsonAsync(new Uri($"{ImportClient.Data(import)}/collections/notes/documents/by-key?id={taken}", UriKind.Relative), new { title }))
        {
            await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        _ = await client.StartAsync(import, ready.GetProperty("preview").GetProperty("id").GetString());
        var failed = await client.WaitAsync(import, "failed", "completed");
        await Assert.That(failed.GetProperty("state").GetString()).IsEqualTo("failed");
        await Assert.That(failed.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("destination_modified");
        await Assert.That(failed.GetProperty("error").GetProperty("retryable").GetBoolean()).IsFalse();
        using (var kept = await client.Client.GetAsync(new Uri($"{ImportClient.Data(import)}/collections/notes/documents/by-key?id={taken}", UriKind.Relative)))
        {
            await Assert.That((await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("title").GetString()).IsEqualTo(title);
        }

        var written = failed.GetProperty("progress").GetProperty("rowsWritten").GetInt64();
        await Assert.That(written).IsGreaterThan(0);

        // The owner removes their document; the resumed copy continues under the same preview and validates.
        using (var deleted = await client.Client.DeleteAsync(new Uri($"{ImportClient.Data(import)}/collections/notes/documents/by-key?id={taken}", UriKind.Relative)))
        {
            await Assert.That(deleted.IsSuccessStatusCode).IsTrue();
        }

        var (resumed, queued) = await client.ResumeAsync(import);
        await Assert.That(resumed).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(queued.GetProperty("confirmedPreviewId").GetString()).IsEqualTo(ready.GetProperty("preview").GetProperty("id").GetString());
        var done = await client.WaitAsync(import, "completed", "failed");
        await Assert.That(done.GetProperty("state").GetString()).IsEqualTo("completed").Because(done.GetRawText());
        await Assert.That(done.GetProperty("validation").GetProperty("passed").GetBoolean()).IsTrue();
        await Assert.That((await client.IdsAsync(import, "notes")).Count).IsEqualTo(1203);
    }

    [Test]
    public async Task A_destination_created_through_management_routes_imports_with_a_project_token()
    {
        await using var client = await ImportClient.StartAsync();
        // The dashboard's path: the signed-in user creates the destination through the Management API.
        var name = "import-" + Primitives.EntityIds.New().ToString().ToLowerInvariant();
        var database = await client.Api.CreateManagedDatabaseAsync(name);
        var import = $"/v1/projects/{database.Project}/databases/{name}/import";
        var file = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "sqlite-notes.db"));

        await using var bound = client.For(database.Project);
        var (created, job) = await bound.CreateAsync(import, "sqlite", file);
        await Assert.That(created).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(job.GetProperty("project").GetString()).IsEqualTo(database.Project);
        await Assert.That(job.GetProperty("database").GetString()).IsEqualTo(name);
        _ = await bound.UploadAsync(import, file);
        var ready = await bound.WaitAsync(import, "ready", "rejected");
        _ = await bound.StartAsync(import, ready.GetProperty("preview").GetProperty("id").GetString());
        var boundDone = await bound.WaitAsync(import, "completed", "failed");
        await Assert.That(boundDone.GetProperty("state").GetString()).IsEqualTo("completed").Because(boundDone.GetRawText());

        // Imported writes and import calls are metered against the destination like any other client traffic.
        await client.Api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
        using var usage = await bound.Client.GetAsync(new Uri($"/v1/projects/{database.Project}/databases/{name}/usage", UriKind.Relative));
        var metered = JsonElement.Parse(await usage.Content.ReadAsStringAsync());
        await Assert.That(metered.GetProperty("writes").GetInt64()).IsGreaterThanOrEqualTo(1203).Because(metered.GetRawText());

        // The documents belong to the management-created database itself, as its owner sees them.
        using var listed = await client.Api.Client.GetAsync(new Uri($"{database.Path}", UriKind.Relative));
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await bound.IdsAsync(import, "notes")).Count).IsEqualTo(1203);

        // A token of another project, or one narrowed to another database, does not reach this import.
        await using var stranger = client.For(Primitives.EntityIds.New().ToString());
        await Assert.That((await stranger.SendAsync(HttpMethod.Get, import.Replace(database.Project, stranger.Project, StringComparison.Ordinal), null)).Status)
            .IsEqualTo(HttpStatusCode.NotFound);
        using var narrowed = client.Api.WithToken(client.Api.Issuer.Mint(Primitives.EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = ImportClient.AllScopes;
            token.Claims["database"] = Primitives.EntityIds.New().ToString();
        }));
        using var narrowedAnswer = await narrowed.GetAsync(new Uri(import, UriKind.Relative));
        await Assert.That(narrowedAnswer.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task CreateCollectionAsync(ImportClient client, string import, string name)
    {
        using var created = await client.Client.PostAsJsonAsync(new Uri($"{ImportClient.Data(import)}/collections", UriKind.Relative), new { name });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    private static Dictionary<string, JsonElement> Tables(JsonElement job) =>
        job.GetProperty("preview").GetProperty("tables").EnumerateArray().ToDictionary(table => table.GetProperty("source").GetString()!);

    private static bool Issue(JsonElement table, string code) =>
        table.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("code").GetString() == code);

    private static string? Mapping(JsonElement table, string column) =>
        table.GetProperty("columns").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == column).GetProperty("mapping").GetString();

    private static string? Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString();

    private static byte[] Corrupt(byte[] file, Faker faker, int count)
    {
        var copy = (byte[])file.Clone();
        for (var i = 0; i < count; i++)
        {
            copy[faker.Random.Int(100, copy.Length - 1)] = faker.Random.Byte();
        }

        return copy;
    }

    // Points the schema page's right-most child at itself when page 1 is interior, else turns a leaf into a self-referencing interior page.
    private static byte[] Cycle(byte[] file)
    {
        var copy = (byte[])file.Clone();
        copy[100] = 5;
        copy[108] = 0;
        copy[109] = 0;
        copy[110] = 0;
        copy[111] = 1;
        return copy;
    }
}
