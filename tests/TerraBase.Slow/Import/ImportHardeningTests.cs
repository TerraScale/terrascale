using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using TerraBase.Features.Import;
using TerraBase.Testing;

namespace TerraBase.Slow.Import;

public sealed class ImportHardeningTests
{
    private const int ImportCancellationDeadlineMilliseconds = 540_000;

    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Import", "Fixtures");

    public static IEnumerable<Func<(string Name, string Format, byte[] Bytes, string Code)>> CraftedSources()
    {
        yield return () => ("two tables share one b-tree", "sqlite", File.ReadAllBytes(Path.Combine(Fixtures, "sqlite-crafted-shared-root.db")), "malformed_source");
        yield return () => ("one table name twice", "sqlite", File.ReadAllBytes(Path.Combine(Fixtures, "sqlite-crafted-duplicate.db")), "malformed_source");
        yield return () => ("overlapping cells", "sqlite", Overlapping(File.ReadAllBytes(Path.Combine(Fixtures, "sqlite-notes.db"))), "malformed_source");
        yield return () => ("1001 tables", "postgres_plain", Text(string.Concat(Enumerable.Range(0, 1001).Select(i => $"CREATE TABLE public.t{i} (a integer);\n"))), "too_many_tables");
        yield return () => ("an unterminated dollar quote", "postgres_plain", Text("CREATE FUNCTION public.f() RETURNS int LANGUAGE sql AS $body$ SELECT 1;\n"), "malformed_source");
        yield return () => ("a pending statement and many backslash lines", "postgres_plain",
            Text("CREATE TABLE public.t (\n" + string.Concat(Enumerable.Repeat("\\x\n", 200_000))), "malformed_source");
        yield return () => ("JSON nested 64 levels", "postgres_plain", Text(
            "CREATE TABLE public.j (id integer NOT NULL, v jsonb);\nCOPY public.j (id, v) FROM stdin;\n1\t" + new string('[', 64) + new string(']', 64) + "\n\\.\n"
            + "ALTER TABLE ONLY public.j ADD CONSTRAINT j_pkey PRIMARY KEY (id);\n"), "invalid_value");
    }

    [Test]
    [MethodDataSource(nameof(CraftedSources))]
    public async Task Crafted_sources_are_refused_with_a_code_and_the_runner_keeps_serving(string name, string format, byte[] bytes, string code)
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        _ = await client.CreateAsync(import, format, bytes);
        _ = await client.UploadAsync(import, bytes);
        var job = await client.WaitAsync(import, "ready", "rejected", "failed");
        await Assert.That(job.GetProperty("state").GetString()).IsEqualTo("rejected").Because($"{name}: {job.GetRawText()[..Math.Min(2000, job.GetRawText().Length)]}");
        await Assert.That(job.GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code).Because(name);

        var valid = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "sqlite-notes.db"));
        var good = await client.CreateDestinationAsync();
        _ = await client.CreateAsync(good, "sqlite", valid);
        _ = await client.UploadAsync(good, valid);
        await Assert.That((await client.WaitAsync(good, "ready", "rejected")).GetProperty("state").GetString()).IsEqualTo("ready").Because(name);
    }

    [Test]
    public async Task Tables_beside_a_virtual_table_and_rows_older_than_a_column_import_as_sqlite_reads_them()
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var file = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "sqlite-crafted-neighbors.db"));
        _ = await client.CreateAsync(import, "sqlite", file);
        _ = await client.UploadAsync(import, file);
        var ready = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready").Because(ready.GetRawText());
        var skipped = ready.GetProperty("preview").GetProperty("skipped").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()).ToList();
        await Assert.That(skipped).Contains("main.notes_data");
        await Assert.That(skipped).DoesNotContain("main.notes_tags");

        _ = await client.StartAsync(import, ready.GetProperty("preview").GetProperty("id").GetString());
        var done = await client.WaitAsync(import, "completed", "failed");
        await Assert.That(done.GetProperty("state").GetString()).IsEqualTo("completed").Because(done.GetRawText());
        await Assert.That((await client.DocumentAsync(import, "notes_tags", ("id", "1"))).GetProperty("tag").GetString()).IsEqualTo("kept");
        var old = await client.DocumentAsync(import, "typed", ("id", "1"));
        await Assert.That(old.GetProperty("n").ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(old.GetProperty("n").GetInt64()).IsEqualTo(5);
        await Assert.That(old.GetProperty("h").GetInt64()).IsEqualTo(16);
        await Assert.That(old.GetProperty("r").GetDouble()).IsEqualTo(2.0);
        await Assert.That((await client.DocumentAsync(import, "typed", ("id", "2"))).GetProperty("r").GetDouble()).IsEqualTo(9.5);
    }

    [Test]
    public async Task An_inheriting_table_a_column_named_exclude_and_a_bom_with_crlf_import()
    {
        await using var client = await ImportClient.StartAsync();
        var import = await client.CreateDestinationAsync();
        var dump = Encoding.UTF8.GetPreamble().Concat(Text(string.Join("\r\n",
            "CREATE TABLE public.parent (", "    id integer NOT NULL,", "    exclude text", ");",
            "CREATE TABLE public.child (", "    extra text", ")", "INHERITS (public.parent);",
            "COPY public.parent (id, exclude) FROM stdin;", "1\tp", "\\.",
            "COPY public.child (id, exclude, extra) FROM stdin;", "2\tc\tx", "\\.",
            "ALTER TABLE ONLY public.parent ADD CONSTRAINT parent_pkey PRIMARY KEY (id);",
            "ALTER TABLE ONLY public.child ADD CONSTRAINT child_pkey PRIMARY KEY (id);", ""))).ToArray();
        _ = await client.CreateAsync(import, "postgres_plain", dump);
        _ = await client.UploadAsync(import, dump);
        var ready = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready").Because(ready.GetRawText());
        _ = await client.StartAsync(import, ready.GetProperty("preview").GetProperty("id").GetString());
        await Assert.That((await client.WaitAsync(import, "completed", "failed")).GetProperty("state").GetString()).IsEqualTo("completed");
        var child = await client.DocumentAsync(import, "child", ("id", "2"));
        await Assert.That((child.GetProperty("exclude").GetString(), child.GetProperty("extra").GetString())).IsEqualTo(("c", "x"));
        await Assert.That((await client.DocumentAsync(import, "parent", ("id", "1"))).GetProperty("exclude").GetString()).IsEqualTo("p");
    }

    [Test]
    public async Task A_forged_state_record_never_starts_a_copy_and_a_failed_first_write_can_be_retried()
    {
        await using var client = await ImportClient.StartAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "pg17-inserts.sql"));
        var retried = await client.CreateDestinationAsync();
        using (var collection = await client.Client.PostAsJsonAsync(new Uri($"{ImportClient.Data(retried)}/collections", UriKind.Relative), new { name = "terrascale-import" }))
        {
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        await Assert.That((await client.CreateAsync(retried, "postgres_plain", bytes)).Status).IsEqualTo(HttpStatusCode.Created);

        var import = await client.CreateDestinationAsync();
        _ = await client.CreateAsync(import, "postgres_plain", bytes);
        _ = await client.UploadAsync(import, bytes);
        var rejected = await client.WaitAsync(import, "ready", "rejected");
        await Assert.That(rejected.GetProperty("state").GetString()).IsEqualTo("rejected");
        var forged = JsonSerializer.Serialize(new
        {
            v = 1,
            rev = "forged",
            state = "queued",
            cancelRequested = false,
            confirmedPreviewId = rejected.GetProperty("preview").GetProperty("id").GetString(),
            progress = (object?)null,
            checkpoint = (object?)null,
            validation = (object?)null,
            error = (object?)null,
            updatedAt = DateTimeOffset.UtcNow,
            startedAt = DateTimeOffset.UtcNow,
            finishedAt = (object?)null,
            retainedUntil = (object?)null,
        });
        using (var put = await client.Client.PutAsJsonAsync(
            new Uri($"{ImportClient.Data(import)}/collections/terrascale-import/documents/by-key?id={ImportNames.StateId}", UriKind.Relative), new { v = 1, rev = "forged", record = forged }))
        {
            await Assert.That(put.IsSuccessStatusCode).IsTrue();
        }

        var failed = await client.WaitAsync(import, "failed", "completed");
        await Assert.That(failed.GetProperty("state").GetString()).IsEqualTo("failed");
        await Assert.That(failed.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("destination_modified");
        await Assert.That(await client.IdsAsync(import, "readings")).IsEmpty();
    }

    [Test]
    [Timeout(ImportCancellationDeadlineMilliseconds)]
    public async Task Cancelling_a_running_copy_stops_it_and_resume_continues_to_a_validated_import(CancellationToken _1)
    {
        await using var client = await ImportClient.StartAsync();
        client.Client.DefaultRequestHeaders.Authorization = new("Bearer", ImportClient.Token(
            client.Api, client.Project, ImportClient.AllScopes, DateTime.UtcNow.AddMilliseconds(ImportCancellationDeadlineMilliseconds)));
        var import = await client.CreateDestinationAsync();
        var faker = new Faker { Random = new Randomizer(1012) };
        var dump = new StringBuilder("CREATE TABLE public.people (id integer NOT NULL, name text, email text, score double precision);\nCOPY public.people (id, name, email, score) FROM stdin;\n");
        const int Rows = 3_000;
        for (var i = 1; i <= Rows; i++)
        {
            dump.Append(i).Append('\t').Append(faker.Name.FullName()).Append('\t').Append(faker.Internet.Email()).Append('\t').Append(faker.Random.Int(0, 1000)).Append('\n');
        }

        dump.Append("\\.\nALTER TABLE ONLY public.people ADD CONSTRAINT people_pkey PRIMARY KEY (id);\n");
        var bytes = Text(dump.ToString());
        _ = await client.CreateAsync(import, "postgres_plain", bytes);
        _ = await client.UploadAsync(import, bytes);
        var ready = await client.WaitAsync(import, "ready", "rejected");
        _ = await client.StartAsync(import, ready.GetProperty("preview").GetProperty("id").GetString());

        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await client.GetAsync(import, token)).GetProperty("progress") is { ValueKind: JsonValueKind.Object } progress && progress.GetProperty("rowsWritten").GetInt64() > 0);
        var (cancel, _) = await client.CancelAsync(import);
        await Assert.That(cancel).IsEqualTo(HttpStatusCode.Accepted);
        var cancelled = await client.WaitAsync(import, "cancelled", "completed");
        await Assert.That(cancelled.GetProperty("state").GetString()).IsEqualTo("cancelled").Because("the copy finished before the cancel landed");
        var partial = cancelled.GetProperty("progress").GetProperty("rowsWritten").GetInt64();
        await Assert.That(partial).IsLessThan(Rows);
        await Assert.That((await client.IdsAsync(import, "people")).Count).IsEqualTo((int)partial);

        _ = await client.ResumeAsync(import);
        JsonElement done = default;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            done = await client.GetAsync(import, token);
            return done.GetProperty("state").GetString() is "completed" or "failed";
        });
        await Assert.That(done.GetProperty("state").GetString()).IsEqualTo("completed").Because(done.GetProperty("error").GetRawText());
        await Assert.That(done.GetProperty("validation").GetProperty("passed").GetBoolean()).IsTrue();
        await Assert.That((await client.IdsAsync(import, "people")).Count).IsEqualTo(Rows);
    }

    [Test]
    public async Task One_organization_cannot_take_the_whole_staging_budget_and_another_destination_can_import()
    {
        await using var client = await ImportClient.StartAsync();
        const long Declared = 600L * 1024 * 1024;
        var first = await client.CreateDestinationAsync();
        var second = await client.CreateDestinationAsync();
        foreach (var import in new[] { first, second })
        {
            var (created, _) = await client.SendAsync(HttpMethod.Post, import, new { format = "postgres_plain", sourceBytes = Declared, sourceSha256 = new string('0', 64) });
            await Assert.That(created).IsEqualTo(HttpStatusCode.Created);
        }

        await Assert.That((await client.PartAsync(first, 0, [(byte)'-'])).Status).IsEqualTo(HttpStatusCode.OK);
        var (refused, body) = await client.PartAsync(second, 0, [(byte)'-']);
        await Assert.That(refused).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("capacity");

        var name = "import-" + Primitives.EntityIds.New().ToString().ToLowerInvariant();
        using (var created = await client.Client.PostAsJsonAsync(new Uri($"/v1/projects/{client.Project}/databases", UriKind.Relative), new { name }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }
        var destination = $"/v1/projects/{client.Project}/databases/{name}/import";
        var source = Text("{\"_id\":{\"$oid\":\"0123456789abcdef01234567\"},\"n\":1}\n{\"n\":2}\n");
        await Assert.That((await client.CreateAsync(destination, "ndjson", source, new { collection = "items" })).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await client.UploadAsync(destination, source)).Status).IsEqualTo(HttpStatusCode.OK);
        var ready = await client.WaitAsync(destination, "ready", "rejected");
        await Assert.That(ready.GetProperty("state").GetString()).IsEqualTo("ready").Because(ready.GetRawText());
        await client.StartAsync(destination, ready.GetProperty("preview").GetProperty("id").GetString());
        var done = await client.WaitAsync(destination, "completed", "failed");
        await Assert.That(done.GetProperty("state").GetString()).IsEqualTo("completed").Because(done.GetRawText());
        await Assert.That((await client.IdsAsync(destination, "items")).Count).IsEqualTo(2);
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Overlapping(byte[] file)
    {
        var copy = (byte[])file.Clone();
        var pointers = 100 + (copy[100] is 13 or 10 ? 8 : 12);
        copy[pointers + 2] = copy[pointers];
        copy[pointers + 3] = copy[pointers + 1];
        return copy;
    }
}
