using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Errors;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Queries;
using TerraBase.Primitives;
using TerraBase.Testing;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Fast.Management;

public sealed class RequestLogApiTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Request_identity_matches_success_failure_and_authorized_logs(bool trusted)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var project = Project(api, database.Project, "databases:read");
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            var supplied = EntityIds.New().ToString();
            using var request = new HttpRequestMessage(method, ManagementApi.Uri(method == HttpMethod.Get
                ? database.Data + "/collections" : ManagementApi.DocumentPath(database, "people", "validation")));
            request.Headers.Add(RequestIdentity.Header, supplied);
            request.Headers.Add(RequestIdentity.ProofHeader, RequestIdentity.Sign(supplied, trusted ? PeerTls.Secret : new string('0', 64)));
            if (method == HttpMethod.Put) { request.Content = new StringContent("{", System.Text.Encoding.UTF8, "application/json"); }
            using var response = await data.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
            var id = response.Headers.GetValues(RequestIdentity.Header).Single();
            await Assert.That(EntityIds.IsValid(id)).IsTrue();
            await Assert.That(id == supplied).IsEqualTo(trusted);
            await Assert.That(response.Headers.Contains(RequestIdentity.ProofHeader)).IsFalse();
            var rows = (await ReadAsync(project, database)).GetProperty("items").EnumerateArray();
            await Assert.That(rows.Any(row => row.GetProperty("requestId").GetString() == id
                && row.GetProperty("status").GetInt32() == (int)response.StatusCode)).IsTrue();
        }
    }

    [Test]
    public async Task Authorized_requests_include_failures_and_committed_operations_without_private_values()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var project = Project(api, database.Project, "databases:read");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using (var put = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "private-key")), new { value = "private-payload" }))
        {
            put.EnsureSuccessStatusCode();
        }
        using (var missing = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "missing-key"))))
        {
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        using (var query = await data.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/collections/people/query"), new { filter = new { eq = new object[] { "/value", "private-payload" } } }))
        {
            query.EnsureSuccessStatusCode();
        }
        var body = await ReadAsync(project, database);
        var rows = body.GetProperty("items").EnumerateArray().ToArray();
        await Assert.That(rows.Any(row => row.GetProperty("status").GetInt32() == 404)).IsTrue();
        await Assert.That(rows.Any(row => row.GetProperty("method").GetString() == "PUT" && row.GetProperty("operations").GetInt64() == 1)).IsTrue();
        await Assert.That(rows.Any(row => row.GetProperty("isQuery").GetBoolean() && row.GetProperty("operations").GetInt64() == 1)).IsTrue();
        foreach (var row in rows)
        {
            await Assert.That(EntityIds.IsValid(row.GetProperty("id").GetString())).IsTrue();
            await Assert.That(row.GetProperty("latencyMs").GetDouble()).IsGreaterThanOrEqualTo(0);
            await Assert.That(row.GetProperty("route").GetString()!).DoesNotContain("?");
        }
        await Assert.That(body.GetRawText()).DoesNotContain("private-payload");
        await Assert.That(body.GetRawText()).DoesNotContain(ManagementApi.DocumentId("private-key"));
        var failures = await ReadAsync(project, database, "?status=4xx");
        await Assert.That(failures.GetProperty("items").EnumerateArray().All(row => row.GetProperty("status").GetInt32() / 100 == 4)).IsTrue();
        await Assert.That(failures.GetProperty("items").GetArrayLength()).IsGreaterThan(0);
        await Assert.That((await ReadAsync(project, database)).GetProperty("items").GetArrayLength()).IsEqualTo(rows.Length)
            .Because("reading logs must not add more requests to the log");
    }

    [Test]
    public async Task Project_and_database_grants_are_enforced_before_reading_logs()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var path = ManagementApi.Uri(database.Data + "/logs");
        using var foreign = Project(api, EntityIds.New().ToString(), "databases:read");
        using var wrongProject = await foreign.GetAsync(path);
        await Assert.That(wrongProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var restricted = Project(api, database.Project, "databases:read", EntityIds.New().ToString());
        using var wrongDatabase = await restricted.GetAsync(path);
        await Assert.That(wrongDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var noScope = Project(api, database.Project, "documents:read");
        using var forbidden = await noScope.GetAsync(path);
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var allowed = Project(api, database.Project, "databases:read");
        using var invalid = await allowed.GetAsync(ManagementApi.Uri(database.Data + "/logs?status=error"));
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var other = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "other");
        await Assert.That((await ReadAsync(allowed, other)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Recent_window_is_bounded_persistent_and_expires_after_24_hours()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-request-logs-").FullName;
        var time = new FakeTimeProvider();
        ManagedDatabaseRef database;
        try
        {
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: time))
            {
                database = await api.CreateManagedDatabaseAsync();
                using var project = Project(api, database.Project, "databases:read");
                using var data = await api.CreateClientAsync(database.Organization, "documents:read");
                for (var i = 0; i < 110; i++)
                {
                    time.Advance(TimeSpan.FromMilliseconds(20));
                    using var response = await data.GetAsync(ManagementApi.Uri(database.Data + "/collections"));
                    response.EnsureSuccessStatusCode();
                }
                var page = await ReadAsync(project, database);
                await Assert.That(page.GetProperty("items").GetArrayLength()).IsEqualTo(100);
                await Assert.That(page.GetProperty("truncated").GetBoolean()).IsTrue();
                await api.Server.Services.GetRequiredService<IRequestLogs>().FlushAsync(CancellationToken.None);
                await Assert.That((await ReadAsync(project, database)).GetProperty("items").GetArrayLength()).IsEqualTo(100);
            }
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: time))
            {
                using var project = Project(api, database.Project, "databases:read");
                var restored = await ReadAsync(project, database);
                await Assert.That(restored.GetProperty("items").GetArrayLength()).IsEqualTo(100);
                time.Advance(TimeSpan.FromHours(25));
                // A new token is needed after advancing beyond the old token's lifetime.
                using var current = Project(api, database.Project, "databases:read");
                await Assert.That((await ReadAsync(current, database)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task Cursor_reads_list_settled_requests_oldest_first_and_report_gaps()
    {
        var time = new FakeTimeProvider();
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var start = Ulid.NewUlid(time.GetUtcNow(), new byte[10]).ToString();
        async Task RequestAsync(int count)
        {
            using var data = await api.CreateClientAsync(database.Organization, "documents:read");
            for (var i = 0; i < count; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(20));
                using var response = await data.GetAsync(ManagementApi.Uri(database.Data + "/collections"));
                response.EnsureSuccessStatusCode();
            }
        }

        await RequestAsync(3);
        using (var project = Project(api, database.Project, "databases:read"))
        {
            // Unsettled requests stay unread; the cursor does not pass them.
            var early = await ReadAsync(project, database, "?after=" + start);
            await Assert.That(early.GetProperty("items").GetArrayLength()).IsEqualTo(0);
            await Assert.That(early.GetProperty("nextCursor").GetString()).IsEqualTo(start);
        }
        time.Advance(TimeSpan.FromSeconds(31));
        using (var project = Project(api, database.Project, "databases:read"))
        {
            var page = await ReadAsync(project, database, "?after=" + start);
            var ids = page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
            await Assert.That(ids.Length).IsEqualTo(3);
            await Assert.That(ids).IsOrderedBy(id => id, StringComparer.Ordinal);
            await Assert.That(page.GetProperty("gap").GetBoolean()).IsFalse();
            var next = page.GetProperty("nextCursor").GetString()!;
            await Assert.That(string.CompareOrdinal(next, ids[^1])).IsGreaterThan(0);
            await Assert.That((await ReadAsync(project, database, "?after=" + next)).GetProperty("items").GetArrayLength()).IsEqualTo(0);

            // Overflowing the retained window past the cursor is reported, not silently skipped.
            await RequestAsync(110);
            time.Advance(TimeSpan.FromSeconds(31));
            var overflow = await ReadAsync(project, database, "?after=" + next);
            await Assert.That(overflow.GetProperty("items").GetArrayLength()).IsEqualTo(100);
            await Assert.That(overflow.GetProperty("gap").GetBoolean()).IsTrue();
            var caughtUp = await ReadAsync(project, database, "?after=" + overflow.GetProperty("nextCursor").GetString());
            await Assert.That(caughtUp.GetProperty("gap").GetBoolean()).IsFalse();

            using var invalid = await project.GetAsync(ManagementApi.Uri(database.Data + "/logs?after=not-a-ulid"));
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        time.Advance(TimeSpan.FromHours(25));
        using (var project = Project(api, database.Project, "databases:read"))
        {
            await Assert.That((await ReadAsync(project, database, "?after=" + start)).GetProperty("gap").GetBoolean()).IsTrue();
        }
    }

    private static HttpClient Project(ManagementApi api, string project, string scope, string? database = null) =>
        api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = scope;
            if (database is not null) { token.Claims["database"] = database; }
        }));

    private static async Task<JsonElement> ReadAsync(HttpClient client, ManagedDatabaseRef database, string query = "")
    {
        using var response = await client.GetAsync(ManagementApi.Uri(database.Data + "/logs" + query));
        response.EnsureSuccessStatusCode();
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    private sealed class ThrowingShapes : IQueryShapeTracker
    {
        public void Record(TenantContext? database, string collection, string shape, TimeSpan latency, long rowsScanned) =>
            throw new InvalidOperationException("injected request failure");

        public ValueTask<QueryShapeReadResult> ReadAsync(TenantContext? database, CancellationToken cancellationToken) =>
            ValueTask.FromResult<QueryShapeReadResult>(new Unavailable("injected statistics failure"));
    }
}
