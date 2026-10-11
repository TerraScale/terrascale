using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class CommitConcurrencyTests
{
    private const string Organizations = "/v1/management/organizations";
    private const string Commits = "/v1/management/commits";
    private const string Versions = "/v1/management/versions";
    private const string Documents = "/v1/management/documents";
    private const string ReplayedHeader = "TerraBase-Commit-Replayed";
    private const int Writers = 12;
    private static readonly Faker Faker = new();

    [Test]
    public async Task Concurrent_redemptions_of_one_invitation_accept_it_once()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var (email, hash) = (Email(), Hash(Token()));
        var invited = await CommitAsync(api.Client, new { op = "invite", organization, email, token_hash = hash });
        var users = Enumerable.Range(0, 8).Select(_ => User()).ToList();
        var clients = users.Select(user => api.As(user, email)).ToList();
        try
        {
            var replies = await Task.WhenAll(clients.Select(client => CommitAsync(client, new { op = "accept_invitation", token_hash = hash })));
            var winners = Enumerable.Range(0, users.Count).Where(index => replies[index].Status == HttpStatusCode.OK).Select(index => users[index]).ToList();
            var members = await GetAsync(api.Client, $"{Organizations}/{organization}/members");

            await Assert.That(invited.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(winners.Count).IsEqualTo(1);
            await Assert.That(replies.Where(reply => reply.Status != HttpStatusCode.OK).Select(reply => (reply.Status, reply.Code)))
                .All().Satisfy(failure => failure.IsEqualTo((HttpStatusCode.Conflict, "invitation_closed")));
            await Assert.That(Roles(members.Json)).IsEquivalentTo([$"{api.User}=owner", $"{winners[0]}=member"]);
        }
        finally
        {
            clients.ForEach(client => client.Dispose());
        }
    }

    [Test]
    public async Task Concurrent_invitations_never_exceed_the_member_limit()
    {
        const int limit = 3;
        await using var api = await ManagementApi.StartAsync(managementCredential: true);
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        await api.SetPlanAsync(api.User, organization, "enterprise");
        var invitations = Enumerable.Range(0, Writers).Select(_ => (Email: Email(), Hash: Hash(Token()))).ToList();

        var replies = await Task.WhenAll(invitations.Select(invitation => CommitAsync(api.Client,
            new { op = "invite", organization, email = invitation.Email, token_hash = invitation.Hash },
            new { op = "limit", organization, members = limit })));
        var members = await GetAsync(api.Client, $"{Organizations}/{organization}/members");
        var pending = await GetAsync(api.Client, $"{Organizations}/{organization}/invitations");
        var pendingCount = Items(pending.Json).Count(item => item.GetProperty("status").GetString() == "pending");

        // The owner is one member, so two invitations fill the limit; the rest are refused.
        await Assert.That(replies.Count(reply => reply.Status == HttpStatusCode.OK)).IsEqualTo(limit - 1);
        await Assert.That(replies.Where(reply => reply.Status != HttpStatusCode.OK).Select(reply => (reply.Status, reply.Code, reply.Op)))
            .All().Satisfy(failure => failure.IsEqualTo((HttpStatusCode.Conflict, "limit_exceeded", (int?)1)));
        await Assert.That(Items(members.Json).Count() + pendingCount).IsEqualTo(limit);
    }

    [Test]
    public async Task Concurrent_database_creations_never_exceed_the_database_limit()
    {
        const int limit = 3;
        await using var api = await ManagementApi.StartAsync(managementCredential: true);
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        await api.SetPlanAsync(api.User, organization, "enterprise");
        var project = await api.CreateProjectAsync(organization, ProjectName());
        var names = Enumerable.Range(0, Writers).Select(index => "db_" + index.ToString(CultureInfo.InvariantCulture)).ToList();

        var replies = await Task.WhenAll(names.Select(name => CommitAsync(api.Client,
            new { op = "create_database", organization, project, name },
            new { op = "limit", organization, databases = limit })));
        var created = Enumerable.Range(0, names.Count).Where(index => replies[index].Status == HttpStatusCode.OK).ToList();
        var listed = await GetAsync(api.Client, $"{Organizations}/{organization}/projects/{project}/databases");
        var listedIds = Strings(listed.Json, "id").ToList();

        await Assert.That(created.Count).IsEqualTo(limit);
        await Assert.That(replies.Where(reply => reply.Status != HttpStatusCode.OK).Select(reply => (reply.Status, reply.Code, reply.Op)))
            .All().Satisfy(failure => failure.IsEqualTo((HttpStatusCode.Conflict, "limit_exceeded", (int?)1)));
        await Assert.That(Strings(listed.Json, "name")).IsEquivalentTo(created.Select(index => names[index]));
        await Assert.That(listedIds).IsEquivalentTo(created.Select(index => Id(replies[index].Value(0))));
        // The refused creations removed the files they had made.
        await Assert.That(DatabaseFiles(api.Server.DataDirectory, organization, project)).IsEquivalentTo(listedIds);
        await Assert.That(CreatingFiles(api.Server.DataDirectory, organization, project)).IsEmpty();
    }

    [Test]
    public async Task An_organization_past_its_database_limit_keeps_its_databases_but_cannot_add_more()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var project = await api.CreateProjectAsync(organization, ProjectName());
        List<string> databases =
        [
            await api.CreateDatabaseAsync(organization, project, "first"),
            await api.CreateDatabaseAsync(organization, project, "second"),
            await api.CreateDatabaseAsync(organization, project, "third"),
        ];

        // Three databases on a plan of one: removing one still leaves the organization past its limit, and is allowed.
        var deleted = await CommitAsync(api.Client,
            new { op = "delete_database", organization, project, database = databases[0] },
            new { op = "limit", organization, databases = 1 });
        var added = await CommitAsync(api.Client,
            new { op = "create_database", organization, project, name = "fourth" },
            new { op = "limit", organization, databases = 1 });
        var listed = await GetAsync(api.Client, $"{Organizations}/{organization}/projects/{project}/databases");

        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((added.Status, added.Code, added.Op)).IsEqualTo((HttpStatusCode.Conflict, "limit_exceeded", 1));
        await Assert.That(Strings(listed.Json, "id")).IsEquivalentTo(databases.Skip(1));
        await Assert.That(DatabaseFiles(api.Server.DataDirectory, organization, project)).IsEquivalentTo(databases.Skip(1));
        await Assert.That(CreatingFiles(api.Server.DataDirectory, organization, project)).IsEmpty();
    }

    [Test]
    public async Task A_committed_database_creation_survives_a_restart_and_is_replayed_not_reapplied()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-commit-restart-").FullName;
        try
        {
            var owner = User();
            var id = NewId();
            string organization, project, stale, body;
            Reply first;
            await using (var api = await StartAsync(dataDirectory))
            {
                using var client = api.As(owner);
                organization = await api.CreateOrganizationAsync(OrganizationName(), client);
                project = await api.CreateProjectAsync(organization, ProjectName(), client);
                // Read before the commit below changes the organization: stale from then on.
                stale = await VersionAsync(client, organization);
                body = Body(id, new { op = "create_database", organization, project, name = "orders" });
                first = await SendAsync(client, body);
            }

            await using (var api = await StartAsync(dataDirectory))
            {
                using var client = api.As(owner);
                var recorded = await GetAsync(client, $"{Commits}/{id}");
                var replayed = await SendAsync(client, body);
                var failed = await CommitAsync(client,
                    new { op = "create_database", organization, project, name = "scratch" },
                    new { op = "fence", organization, version = stale });
                var listed = await GetAsync(client, $"{Organizations}/{organization}/projects/{project}/databases");
                var database = Id(first.Value(0));

                await Assert.That(first.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(first.Value(0).GetProperty("name").GetString()).IsEqualTo("orders");
                await Assert.That(recorded.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(JsonElement.DeepEquals(recorded.Json, first.Json)).IsTrue();
                await Assert.That(replayed.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(replayed.Replayed).IsTrue();
                await Assert.That(replayed.Body).IsEqualTo(first.Body);
                await Assert.That((failed.Status, failed.Code, failed.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 1));
                // One database: the replay created no second one and the failed commit left none behind.
                await Assert.That(Strings(listed.Json, "id")).IsEquivalentTo([database]);
                await Assert.That(DatabaseFiles(dataDirectory, organization, project)).IsEquivalentTo([database]);
                await Assert.That(CreatingFiles(dataDirectory, organization, project)).IsEmpty();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Accepting_cannot_grow_active_members_past_their_limit()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var member = await api.AddMemberAsync(api.Client, organization, "member");
        var (email, hash) = (Email(), Hash(Token()));
        var invited = await CommitAsync(api.Client, new { op = "invite", organization, email, token_hash = hash, role = "viewer" });
        var invitee = User();
        using var client = api.As(invitee, email);

        // Two members on a plan of two: the pending invitation is already counted by members, so only active_members refuses it.
        var refused = await CommitAsync(client,
            new { op = "accept_invitation", token_hash = hash },
            new { op = "limit", organization, active_members = 2 });
        var accepted = await CommitAsync(client,
            new { op = "accept_invitation", token_hash = hash },
            new { op = "limit", organization, active_members = 3 });
        var members = await GetAsync(api.Client, $"{Organizations}/{organization}/members");

        await Assert.That(invited.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((refused.Status, refused.Code, refused.Op)).IsEqualTo((HttpStatusCode.Conflict, "limit_exceeded", 1));
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accepted.Result(1).GetProperty("count").GetString()).IsEqualTo("3");
        await Assert.That(Roles(members.Json)).IsEquivalentTo([$"{api.User}=owner", $"{member}=member", $"{invitee}=viewer"]);
    }

    [Test]
    public async Task A_commit_interrupted_after_recording_its_database_intent_leaves_nothing_after_a_restart()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-commit-crash-").FullName;
        try
        {
            var owner = User();
            var id = NewId();
            string organization, project, body;
            Reply interrupted;
            await using (var api = await StartAsync(dataDirectory))
            {
                using var client = api.As(owner);
                organization = await api.CreateOrganizationAsync(OrganizationName(), client);
                project = await api.CreateProjectAsync(organization, ProjectName(), client);
            }

            // The process stops right after the intent commits, before the files and the commit: nothing cleans up.
            await using (var api = await ManagementApi.StartAsync(
                settings => settings["data-directory"] = dataDirectory,
                services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
                {
                    if (step == "create.intent")
                    {
                        throw new InvalidOperationException("interrupted commit");
                    }
                })))
            {
                using var client = api.As(owner);
                body = Body(id, new { op = "create_database", organization, project, name = "orders" }, new { op = "rename_project", organization, project, name = ProjectName() });
                interrupted = await SendAsync(client, body);
            }

            await using (var api = await StartAsync(dataDirectory))
            {
                using var client = api.As(owner);
                var recorded = await GetAsync(client, $"{Commits}/{id}");
                var listed = await GetAsync(client, $"{Organizations}/{organization}/projects/{project}/databases");
                var filesAfterRecovery = ProjectFiles(dataDirectory, organization, project);
                // It never committed, so resending the same id and body applies it now, once.
                var resent = await SendAsync(client, body);
                var relisted = await GetAsync(client, $"{Organizations}/{organization}/projects/{project}/databases");

                await Assert.That(interrupted.Status).IsEqualTo(HttpStatusCode.InternalServerError);
                await Assert.That(recorded.Status).IsEqualTo(HttpStatusCode.NotFound);
                await Assert.That(Strings(listed.Json, "id")).IsEmpty();
                await Assert.That(filesAfterRecovery).IsEmpty();
                await Assert.That(resent.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(resent.Replayed).IsFalse();
                await Assert.That(Strings(relisted.Json, "id")).IsEquivalentTo([Id(resent.Value(0))]);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_committed_delete_interrupted_while_removing_files_is_finished_at_the_next_start()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-commit-delete-").FullName;
        try
        {
            var owner = User();
            var id = NewId();
            string organization, project, database, body;
            Reply interrupted;
            await using (var api = await StartAsync(dataDirectory))
            {
                using var client = api.As(owner);
                organization = await api.CreateOrganizationAsync(OrganizationName(), client);
                project = await api.CreateProjectAsync(organization, ProjectName(), client);
                database = await api.CreateDatabaseAsync(organization, project, "orders", client);
            }

            // The commit is durable; the process stops after removing the main file, before the sidecars and the intent.
            await using (var api = await ManagementApi.StartAsync(
                settings => settings["data-directory"] = dataDirectory,
                services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
                {
                    if (step == "delete.main_file")
                    {
                        throw new InvalidOperationException("interrupted cleanup");
                    }
                })))
            {
                using var client = api.As(owner);
                body = Body(id, new { op = "delete_database", organization, project, database });
                interrupted = await SendAsync(client, body);
            }

            await using (var api = await StartAsync(dataDirectory))
            {
                using var client = api.As(owner);
                var recorded = await GetAsync(client, $"{Commits}/{id}");
                var replayed = await SendAsync(client, body);
                var listed = await GetAsync(client, $"{Organizations}/{organization}/projects/{project}/databases");

                await Assert.That(interrupted.Status).IsEqualTo(HttpStatusCode.InternalServerError);
                await Assert.That(recorded.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(replayed.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(replayed.Replayed).IsTrue();
                await Assert.That(Strings(listed.Json, "id")).IsEmpty();
                await Assert.That(ProjectFiles(dataDirectory, organization, project).Where(name => name.Contains(database, StringComparison.Ordinal))).IsEmpty();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Dashboard_documents_are_reachable_only_with_the_management_service_credential()
    {
        await using var api = await ManagementApi.StartAsync(managementCredential: true);
        using var service = api.Service(api.User);
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var document = NewId();
        var written = await CommitAsync(service, new { op = "create", collection = "workspaces", id = document, document = new { organization, note = Faker.Lorem.Sentence() } });
        using var data = api.DataClient(await api.Server.IssueAsync(organization, "documents:read", "documents:write"));

        var dashboard = await GetAsync(service, $"{Documents}/workspaces/by-key?id={System.Uri.EscapeDataString(document)}");
        List<HttpStatusCode> withKey =
        [
            await StatusAsync(data.GetAsync(Uri($"{Documents}/workspaces/by-key?id={System.Uri.EscapeDataString(document)}"))),
            await StatusAsync(data.PostAsJsonAsync(Uri($"{Documents}/workspaces/query"), new { limit = 10 })),
            await StatusAsync(data.PostAsJsonAsync(Uri(Commits), new { id = NewId(), ops = new object[] { new { op = "delete", collection = "workspaces", id = document } } })),
            await StatusAsync(data.GetAsync(Uri($"{Commits}/{written.Id}"))),
        ];
        // A signed-in user's own ts-auth token reaches Management, but never the dashboard's documents.
        var userRead = await GetAsync(api.Client, $"{Documents}/workspaces/by-key?id={System.Uri.EscapeDataString(document)}");
        using var userQuery = await api.Client.PostAsJsonAsync(Uri($"{Documents}/workspaces/query"), new { limit = 10 });
        var userWrite = await CommitAsync(api.Client,
            new { op = "rename_organization", organization, name = OrganizationName() },
            new { op = "delete", collection = "workspaces", id = document });
        var after = await GetAsync(service, $"{Documents}/workspaces/by-key?id={System.Uri.EscapeDataString(document)}");

        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(dashboard.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(withKey).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Unauthorized));
        await Assert.That((userRead.Status, userRead.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden"));
        await Assert.That(userQuery.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((userWrite.Status, userWrite.Code, userWrite.Op)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden", (int?)1));
        await Assert.That(after.ETag).IsEqualTo(dashboard.ETag);
    }

    private static Task<ManagementApi> StartAsync(string dataDirectory) =>
        ManagementApi.StartAsync(settings => settings["data-directory"] = dataDirectory);

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    private static string NewId() => EntityIds.New().ToString();

    private static string User() => EntityIds.New().ToString();

    // The suffix keeps two addresses of one test from colliding by chance.
    private static string Email() => Faker.Internet.Email(uniqueSuffix: Faker.Random.AlphaNumeric(6));

    private static string OrganizationName() => Faker.Company.CompanyName() + " " + Faker.Random.AlphaNumeric(6);

    private static string ProjectName() => "Project " + Faker.Random.AlphaNumeric(8);

    // An invitation token as the dashboard generates it; TerraBase only ever sees its hash.
    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Id(JsonElement resource) => resource.GetProperty("id").GetString()!;

    // The database ids with a main file in the project's directory.
    private static IEnumerable<string> DatabaseFiles(string dataDirectory, string organization, string project) =>
        ProjectFiles(dataDirectory, organization, project).Where(name => name.EndsWith(".db", StringComparison.Ordinal)).Select(name => name[..^".db".Length]);

    // Temporary files of database creations that never finished.
    private static IEnumerable<string> CreatingFiles(string dataDirectory, string organization, string project) =>
        ProjectFiles(dataDirectory, organization, project).Where(name => name.Contains(".creating", StringComparison.Ordinal));

    private static List<string> ProjectFiles(string dataDirectory, string organization, string project)
    {
        var directory = Path.Combine(dataDirectory, organization, project);
        return Directory.Exists(directory) ? [.. Directory.GetFiles(directory).Select(Path.GetFileName).OfType<string>()] : [];
    }

    private static async Task<string> VersionAsync(HttpClient client, string organization)
    {
        var versions = await GetAsync(client, $"{Versions}?organization={organization}");
        return versions.Json.GetProperty("organizations").GetProperty(organization).GetString()!;
    }

    private static string Body(string id, params object[] ops) => JsonSerializer.Serialize(new { id, ops });

    private static Task<Reply> CommitAsync(HttpClient client, params object[] ops) => SendAsync(client, Body(NewId(), ops));

    // Posts a commit body exactly as given, so that a retry can resend the same bytes.
    private static async Task<Reply> SendAsync(HttpClient client, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Uri(Commits), content);
        return await ReadAsync(response, JsonSerializer.Deserialize<JsonElement>(body).GetProperty("id").GetString());
    }

    private static async Task<Reply> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(Uri(path));
        return await ReadAsync(response);
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    private static async Task<Reply> ReadAsync(HttpResponseMessage response, string? id = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        var json = response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) == true
            ? JsonSerializer.Deserialize<JsonElement>(body)
            : default;
        var replayed = response.Headers.TryGetValues(ReplayedHeader, out var values) && values.Contains("true", StringComparer.Ordinal);
        return new Reply(response.StatusCode, body, json, response.Headers.ETag?.Tag.Trim('"'), replayed, id);
    }

    private static JsonElement.ArrayEnumerator Items(JsonElement page) => page.GetProperty("items").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement page, string property) => Items(page).Select(item => item.GetProperty(property).GetString()!);

    // "userId=role" of every member on a page.
    private static IEnumerable<string> Roles(JsonElement page) =>
        Items(page).Select(member => $"{member.GetProperty("userId").GetString()}={member.GetProperty("role").GetString()}");

    /// <summary>A response read to the end; <see cref="Id"/> is the commit id that was sent, if any.</summary>
    private sealed record Reply(HttpStatusCode Status, string Body, JsonElement Json, string? ETag, bool Replayed, string? Id)
    {
        public string? Code => Error is { } error ? error.GetProperty("code").GetString() : null;

        public int? Op => Error is { } error && error.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.Number ? op.GetInt32() : null;

        private JsonElement? Error => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error) ? error : null;

        public JsonElement Result(int index) => Json.GetProperty("results")[index];

        public JsonElement Value(int index) => Result(index).GetProperty("value");
    }
}
