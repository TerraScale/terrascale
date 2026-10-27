using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Tenancy;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class CommitApiTests
{
    private const string Organizations = "/v1/management/organizations";
    private const string Commits = "/v1/management/commits";
    private const string Versions = "/v1/management/versions";
    private const string Documents = "/v1/management/documents";
    private const string Lookup = "/v1/management/invitations/lookup";
    private const string ReplayedHeader = "TerraBase-Commit-Replayed";
    private static readonly Faker Faker = new();
    private static readonly string[] StatusField = ["status"];
    private static readonly string[] RecoveryReplicas = ["sjc"];

    [Test]
    public async Task Identical_catalog_creates_initialize_missing_storage_without_rewriting_the_registration()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        using var service = api.Service(api.User);
        var (organization, project, database) = (NewId(), NewId(), NewId());
        object[] ops =
        [
            new { op = "create_organization", id = organization, name = "recovery" },
            new { op = "create_project", organization, id = project, name = "recovery" },
            new { op = "create_database", organization, project, id = database, name = "recovery", replica_regions = RecoveryReplicas },
        ];
        var initial = await CommitAsync(service, ops);
        await Assert.That(initial.Status).IsEqualTo(HttpStatusCode.OK);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, database);
        var system = await engine.OpenSystemDatabaseAsync(token);
        var collections = new[] { "organizations", $"organizations/{organization}/projects", $"projects/{project}/databases" };
        var ids = new[] { organization, project, database };
        var versions = new DocumentVersion?[3];
        await using (var read = await system.BeginTransactionAsync(write: false, token))
        {
            for (var index = 0; index < ids.Length; index++)
            {
                versions[index] = await read.GetVersionAsync(collections[index], ids[index], token);
            }
        }

        await Assert.That(await engine.DeleteDatabaseAsync(tenant, token)).IsTrue();
        await Assert.That(await engine.OpenDatabaseAsync(tenant, token)).IsNull();
        var repeated = await CommitAsync(service, ops);
        await Assert.That(repeated.Status).IsEqualTo(HttpStatusCode.OK).Because(repeated.Body);
        await Assert.That(await engine.OpenDatabaseAsync(tenant, token)).IsNotNull();
        await using var after = await system.BeginTransactionAsync(write: false, token);
        for (var index = 0; index < ids.Length; index++)
        {
            await Assert.That(repeated.Value(index).GetRawText()).IsEqualTo(initial.Value(index).GetRawText());
            await Assert.That(await after.GetVersionAsync(collections[index], ids[index], token)).IsEqualTo(versions[index]);
        }
    }

    [Test]
    [Arguments("organization")]
    [Arguments("project")]
    [Arguments("database")]
    [Arguments("placement")]
    [Arguments("deleted")]
    public async Task Catalog_create_reuse_rejects_different_content_and_deleted_ids(string conflict)
    {
        await using var api = await ManagementApi.StartAsync();
        using var service = api.Service(api.User);
        var (organization, project, database) = (NewId(), NewId(), NewId());
        var initial = await CommitAsync(service,
            new { op = "create_organization", id = organization, name = "original" },
            new { op = "create_project", organization, id = project, name = "original" },
            new { op = "create_database", organization, project, id = database, name = "original", replica_regions = RecoveryReplicas });
        await Assert.That(initial.Status).IsEqualTo(HttpStatusCode.OK);
        if (conflict == "deleted")
        {
            var deleted = await CommitAsync(service,
                new { op = "set_database_regions", organization, project, database, replica_regions = Array.Empty<string>() },
                new { op = "delete_database", organization, project, database });
            await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        }

        var attempted = await CommitAsync(service,
            new { op = "create_organization", id = organization, name = conflict == "organization" ? "changed" : "original" },
            new { op = "create_project", organization, id = project, name = conflict == "project" ? "changed" : "original" },
            new
            {
                op = "create_database",
                organization,
                project,
                id = database,
                name = conflict == "database" ? "changed" : "original",
                replica_regions = new[] { conflict == "placement" ? "fra" : "sjc" }
            });
        await Assert.That((attempted.Status, attempted.Code)).IsEqualTo((HttpStatusCode.Conflict, "id_taken"));
        await Assert.That(attempted.Op).IsEqualTo((int?)(conflict == "organization" ? 0 : conflict == "project" ? 1 : 2));
    }

    [Test]
    public async Task Commit_document_creates_use_the_application_clock_for_monotonic_ULIDs()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: clock);
        using var service = api.Service(api.User);
        var ids = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var commit = await CommitAsync(service,
                new { op = "create", collection = "notes", document = new { value = i * 2 } },
                new { op = "create", collection = "notes", document = new { value = (i * 2) + 1 } });
            await Assert.That(commit.Status).IsEqualTo(HttpStatusCode.OK);
            for (var j = 0; j < 2; j++)
            {
                var id = commit.Result(j).GetProperty("id").GetString()!;
                await Assert.That(EntityIds.IsValid(id)).IsTrue();
                await Assert.That(Ulid.Parse(id).Time).IsEqualTo(clock.GetUtcNow());
                var read = await GetAsync(service, $"{Documents}/notes/by-key?id={System.Uri.EscapeDataString(id)}");
                await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(read.Json.GetProperty("id").GetString()).IsEqualTo(id);
                await Assert.That(read.Json.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo((i * 2) + j);
                await Assert.That(read.ETag).IsEqualTo(Version(commit, j));
                ids.Add(id);
            }
        }

        for (var i = 1; i < ids.Count; i++)
        {
            await Assert.That(string.CompareOrdinal(ids[i - 1], ids[i])).IsLessThan(0);
        }
    }

    [Test]
    public async Task A_commit_creates_an_organization_a_project_and_a_document_naming_both()
    {
        await using var api = await ManagementApi.StartAsync();
        // Dashboard documents need the trusted service: the platform operator acting as the user.
        using var service = api.Service(api.User);
        var (organization, project, document) = (NewId(), NewId(), NewId());
        var (organizationName, projectName) = (OrganizationName(), ProjectName());

        var commit = await CommitAsync(service,
            new { op = "create_organization", id = organization, name = organizationName },
            new { op = "create_project", organization, id = project, name = projectName },
            new { op = "create", collection = "workspaces", id = document, document = new { organization, project, title = Faker.Lorem.Sentence() } });
        var readOrganization = await GetAsync(service, $"{Organizations}/{organization}");
        var readProject = await GetAsync(service, $"{Organizations}/{organization}/projects/{project}");
        var members = await GetAsync(service, $"{Organizations}/{organization}/members");
        var readDocument = await GetAsync(service, $"{Documents}/workspaces/by-key?id={System.Uri.EscapeDataString(document)}");
        var queried = await PostAsync(service, $"{Documents}/workspaces/query", new { filter = new { eq = new object[] { "/project", project } } });
        var versions = await GetAsync(service, $"{Versions}?organization={organization}");

        await Assert.That(commit.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(commit.Json.GetProperty("id").GetString()).IsEqualTo(commit.Id);
        await Assert.That(commit.Value(0).GetProperty("id").GetString()).IsEqualTo(organization);
        await Assert.That(commit.Value(0).GetProperty("name").GetString()).IsEqualTo(organizationName);
        await Assert.That(commit.Value(1).GetProperty("id").GetString()).IsEqualTo(project);
        await Assert.That(commit.Result(2).GetProperty("id").GetString()).IsEqualTo(document);
        await Assert.That(readOrganization.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(readOrganization.Json.GetProperty("name").GetString()).IsEqualTo(organizationName);
        await Assert.That(readProject.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(readProject.Json.GetProperty("name").GetString()).IsEqualTo(projectName);
        // The acting user owns the organization created in the commit.
        await Assert.That(Roles(members.Json)).IsEquivalentTo([$"{api.User}=owner"]);
        await Assert.That(readDocument.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(readDocument.ETag).IsEqualTo(commit.Result(2).GetProperty("version").GetString());
        await Assert.That(readDocument.Json.GetProperty("document").GetProperty("organization").GetString()).IsEqualTo(organization);
        await Assert.That(readDocument.Json.GetProperty("document").GetProperty("project").GetString()).IsEqualTo(project);
        await Assert.That(Strings(queried.Json, "id")).IsEquivalentTo([document]);
        // Nothing changed since the commit, so its versions are the current ones.
        await Assert.That(versions.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OrganizationVersion(versions.Json, organization)).IsEqualTo(OrganizationVersion(commit.Json.GetProperty("versions"), organization));
        await Assert.That(versions.Json.GetProperty("account").GetString())
            .IsEqualTo(commit.Json.GetProperty("versions").GetProperty("account").GetString());
    }

    [Test]
    public async Task Dashboard_documents_are_addressed_by_ULID_in_commits_and_query_key_reads()
    {
        await using var api = await ManagementApi.StartAsync();
        using var service = api.Service(api.User);
        string[] keys = [NewId(), NewId(), NewId()];
        var written = await CommitAsync(service, [.. keys.Select(key => (object)new { op = "put", collection = "settings", id = key, document = new { value = key } })]);
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.OK);
        for (var i = 0; i < keys.Length; i++)
        {
            var read = await GetAsync(service, $"{Documents}/settings/by-key?id={keys[i]}");
            await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(read.Json.GetProperty("id").GetString()).IsEqualTo(keys[i]);
            await Assert.That(read.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(keys[i]);
            await Assert.That(read.ETag).IsEqualTo(Version(written, i));
        }

        // Caller-chosen keys and other id formats are not document ids: writes and reads refuse them.
        string[] invalid = ["settings", "doc_existing", "00112233-4455-6677-8899-aabbccddeeff", keys[0].ToLowerInvariant(), "a/b", "%", "雪😀", "e\u0301"];
        foreach (var key in invalid)
        {
            var refused = await CommitAsync(service, new { op = "put", collection = "settings", id = key, document = new { value = 1 } });
            await Assert.That((refused.Status, refused.Code, refused.Op)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_document_id", (int?)0)).Because(key);
            var read = await GetAsync(service, $"{Documents}/settings/by-key?id={System.Uri.EscapeDataString(key)}");
            await Assert.That((read.Status, read.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_document_id")).Because(key);
        }

        var missing = await GetAsync(service, $"{Documents}/settings/by-key");
        var duplicate = await GetAsync(service, $"{Documents}/settings/by-key?id={keys[0]}&id={keys[1]}");
        var unknown = await GetAsync(service, $"{Documents}/settings/by-key?id={keys[0]}&x=1");
        await Assert.That(missing.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(duplicate.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unknown.Status).IsEqualTo(HttpStatusCode.BadRequest);
        var oldRoute = await GetAsync(service, $"{Documents}/settings/{NewId()}");
        await Assert.That(oldRoute.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_failing_last_operation_publishes_nothing()
    {
        await using var api = await ManagementApi.StartAsync();
        // Dashboard documents need the trusted service: the platform operator acting as the user.
        using var service = api.Service(api.User);
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var originalName = ProjectName();
        var project = await api.CreateProjectAsync(organization, originalName);
        var (existing, added) = (NewId(), NewId());
        var seeded = await CommitAsync(service, new { op = "create", collection = "notes", id = existing, document = new { n = 1 } });
        var updated = await CommitAsync(service, new { op = "put", collection = "notes", id = existing, document = new { n = 2 } });
        var staleDocument = Version(seeded, 0);
        var staleOrganization = await VersionAsync(service, organization);
        using (var renamed = await service.PatchAsJsonAsync(Uri($"{Organizations}/{organization}"), new { name = OrganizationName() }))
        {
            renamed.EnsureSuccessStatusCode();
        }

        var current = await VersionAsync(service, organization);
        var renamedTo = ProjectName();
        var rename = new { op = "rename_project", organization, project, name = renamedTo };
        var put = new { op = "put", collection = "notes", id = added, document = new { n = 3 } };

        var failedDocument = await CommitAsync(service,
            new { op = "fence", organization, version = current },
            rename,
            put,
            new { op = "put", collection = "notes", id = existing, document = new { n = 4 }, if_match = staleDocument });
        var failedFence = await CommitAsync(service, rename, put, new { op = "fence", organization, version = staleOrganization });
        var projectAfter = await GetAsync(service, $"{Organizations}/{organization}/projects/{project}");
        var addedAfter = await GetAsync(service, $"{Documents}/notes/by-key?id={System.Uri.EscapeDataString(added)}");
        var existingAfter = await GetAsync(service, $"{Documents}/notes/by-key?id={System.Uri.EscapeDataString(existing)}");
        var versionAfter = await VersionAsync(service, organization);
        // The same update with current preconditions commits.
        var committed = await CommitAsync(service,
            new { op = "fence", organization, version = current },
            rename,
            put,
            new { op = "put", collection = "notes", id = existing, document = new { n = 4 }, if_match = Version(updated, 0) });
        var projectCommitted = await GetAsync(service, $"{Organizations}/{organization}/projects/{project}");
        var addedCommitted = await GetAsync(service, $"{Documents}/notes/by-key?id={System.Uri.EscapeDataString(added)}");

        await Assert.That((failedDocument.Status, failedDocument.Code, failedDocument.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 3));
        await Assert.That((failedFence.Status, failedFence.Code, failedFence.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 2));
        await Assert.That(projectAfter.Json.GetProperty("name").GetString()).IsEqualTo(originalName);
        await Assert.That(addedAfter.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(existingAfter.ETag).IsEqualTo(Version(updated, 0));
        // Neither failed fence advanced the version.
        await Assert.That(versionAfter).IsEqualTo(current);
        await Assert.That(committed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(projectCommitted.Json.GetProperty("name").GetString()).IsEqualTo(renamedTo);
        await Assert.That(addedCommitted.Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_document_operation_that_outgrows_the_document_limit_names_its_operation_and_writes_nothing()
    {
        await using var api = await ManagementApi.StartAsync();
        using var service = api.Service(api.User);
        var (existing, added) = (NewId(), NewId());
        var half = new string('x', 600 * 1024);
        var seeded = await CommitAsync(service, new { op = "create", collection = "notes", id = existing, document = new { a = half } });

        // Each request fits; the patched document does not.
        var grown = await CommitAsync(service,
            new { op = "create", collection = "notes", id = added, document = new { n = 1 } },
            new { op = "patch", collection = "notes", id = existing, merge = new { b = half } });
        var addedAfter = await GetAsync(service, $"{Documents}/notes/by-key?id={System.Uri.EscapeDataString(added)}");
        var existingAfter = await GetAsync(service, $"{Documents}/notes/by-key?id={System.Uri.EscapeDataString(existing)}");

        await Assert.That(seeded.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((grown.Status, grown.Code, grown.Message, grown.Op))
            .IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "payload_too_large", "a document is at most 1 MiB", (int?)1));
        await Assert.That(addedAfter.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(existingAfter.ETag).IsEqualTo(Version(seeded, 0));
    }

    [Test]
    public async Task A_rest_change_after_the_version_was_read_fails_the_fence_even_with_a_matching_document_version()
    {
        await using var api = await ManagementApi.StartAsync();
        // Dashboard documents need the trusted service: the platform operator acting as the user.
        using var service = api.Service(api.User);
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var document = NewId();
        var created = await CommitAsync(service, new { op = "create", collection = "workspaces", id = document, document = new { organization, title = "a" } });
        var loaded = await VersionAsync(service, organization);
        using var renamed = await service.PatchAsJsonAsync(Uri($"{Organizations}/{organization}"), new { name = OrganizationName() });
        var moved = await VersionAsync(service, organization);
        var write = new { op = "put", collection = "workspaces", id = document, document = new { organization, title = "b" }, if_match = Version(created, 0) };

        var fenced = await CommitAsync(service, new { op = "fence", organization, version = loaded }, write);
        var unchanged = await GetAsync(service, $"{Documents}/workspaces/by-key?id={System.Uri.EscapeDataString(document)}");
        var retried = await CommitAsync(service, new { op = "fence", organization, version = moved }, write);
        var advanced = await VersionAsync(service, organization);

        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(moved).IsNotEqualTo(loaded);
        // Dashboard-only CAS (if_match) would have passed; the Management fence catches the REST rename.
        await Assert.That((fenced.Status, fenced.Code, fenced.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 0));
        await Assert.That(unchanged.ETag).IsEqualTo(Version(created, 0));
        await Assert.That(retried.Status).IsEqualTo(HttpStatusCode.OK);
        // A fence is a compare-and-set: it advances the version it checked.
        await Assert.That(advanced).IsNotEqualTo(moved);
        await Assert.That(OrganizationVersion(retried.Json.GetProperty("versions"), organization)).IsEqualTo(advanced);
    }

    [Test]
    public async Task Concurrent_commits_fencing_the_same_version_commit_exactly_once()
    {
        await using var api = await ManagementApi.StartAsync();
        // Dashboard documents need the trusted service: the platform operator acting as the user.
        using var service = api.Service(api.User);
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        await api.SetPlanAsync(api.User, organization, "enterprise");
        var version = await VersionAsync(service, organization);
        var documents = Enumerable.Range(0, 8).Select(_ => NewId()).ToList();

        // Disjoint documents: only the fence makes the commits conflict.
        var replies = await Task.WhenAll(documents.Select(id => CommitAsync(service,
            new { op = "fence", organization, version },
            new { op = "create", collection = "notes", id, document = new { writer = id } })));
        var reads = await Task.WhenAll(documents.Select(id => GetAsync(service, $"{Documents}/notes/by-key?id={System.Uri.EscapeDataString(id)}")));
        var winners = Enumerable.Range(0, documents.Count).Where(index => replies[index].Status == HttpStatusCode.OK).ToList();
        var stored = Enumerable.Range(0, documents.Count).Where(index => reads[index].Status == HttpStatusCode.OK).ToList();

        await Assert.That(winners.Count).IsEqualTo(1);
        await Assert.That(stored).IsEquivalentTo(winners);
        await Assert.That(replies.Where(reply => reply.Status != HttpStatusCode.OK).Select(reply => (reply.Status, reply.Code, reply.Op)))
            .All().Satisfy(failure => failure.IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", (int?)0)));
    }

    [Test]
    public async Task A_commit_id_makes_a_lost_reply_observable_and_applies_once()
    {
        await using var api = await ManagementApi.StartAsync();
        // Dashboard documents need the trusted service: the platform operator acting as the user.
        using var service = api.Service(api.User);
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        using var stranger = api.As(User());
        var (email, document, id) = (Email(), NewId(), NewId());
        var body = Body(id,
            new { op = "invite", organization, email, role = "member", token_hash = Hash(Token()) },
            new { op = "create", collection = "notes", id = document, document = new { email } });

        var first = await SendAsync(service, body);
        var replayed = await SendAsync(service, body);
        var recorded = await GetAsync(service, $"{Commits}/{id}");
        var foreign = await GetAsync(stranger, $"{Commits}/{id}");
        var reused = await SendAsync(service, Body(id, new { op = "create", collection = "notes", id = NewId(), document = new { n = 1 } }));
        var invitations = await GetAsync(service, $"{Organizations}/{organization}/invitations");
        var read = await GetAsync(service, $"{Documents}/notes/by-key?id={System.Uri.EscapeDataString(document)}");

        var failedId = NewId();
        var failing = Body(failedId, new { op = "create", collection = "notes", id = document, document = new { n = 2 } });
        var failed = await SendAsync(service, failing);
        var failedLookup = await GetAsync(service, $"{Commits}/{failedId}");
        var failedAgain = await SendAsync(service, failing);
        var later = await SendAsync(service, Body(failedId, new { op = "create", collection = "notes", id = NewId(), document = new { n = 3 } }));
        var laterLookup = await GetAsync(service, $"{Commits}/{failedId}");

        await Assert.That(first.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(first.Replayed).IsFalse();
        await Assert.That(replayed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replayed.Replayed).IsTrue();
        await Assert.That(replayed.Body).IsEqualTo(first.Body);
        await Assert.That(recorded.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(JsonElement.DeepEquals(recorded.Json, first.Json)).IsTrue();
        await Assert.That(foreign.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((reused.Status, reused.Code)).IsEqualTo((HttpStatusCode.Conflict, "commit_id_reused"));
        // Applied once: one invitation, and the document still has the version of the first reply.
        await Assert.That(Strings(invitations.Json, "email").Count(listed => string.Equals(listed, email, StringComparison.OrdinalIgnoreCase))).IsEqualTo(1);
        await Assert.That(read.ETag).IsEqualTo(Version(first, 1));
        await Assert.That((failed.Status, failed.Code, failed.Op)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed", 0));
        // Failures record nothing: the id is unknown, and a retry is evaluated again rather than replayed.
        await Assert.That(failedLookup.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((failedAgain.Status, failedAgain.Replayed)).IsEqualTo((HttpStatusCode.PreconditionFailed, false));
        await Assert.That(later.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(laterLookup.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(JsonElement.DeepEquals(laterLookup.Json, later.Json)).IsTrue();
    }

    [Test]
    public async Task A_viewer_reads_but_cannot_change_projects_or_databases()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var project = await api.CreateProjectAsync(organization, ProjectName());
        var viewerId = User();
        var (email, hash) = (Email(), Hash(Token()));
        using var viewer = api.As(viewerId, email);
        var projects = $"{Organizations}/{organization}/projects";

        var invited = await CommitAsync(api.Client, new { op = "invite", organization, email, role = "viewer", token_hash = hash });
        var accepted = await CommitAsync(viewer, new { op = "accept_invitation", token_hash = hash });
        var version = await VersionAsync(viewer, organization);
        var deniedProject = await CommitAsync(viewer,
            new { op = "fence", organization, version },
            new { op = "create_project", organization, name = ProjectName() });
        var deniedDatabase = await CommitAsync(viewer, new { op = "create_database", organization, project, name = "orders" });
        List<HttpStatusCode> deniedRest =
        [
            await StatusAsync(viewer.PostAsJsonAsync(Uri(projects), new { name = ProjectName() })),
            await StatusAsync(viewer.PatchAsJsonAsync(Uri($"{projects}/{project}"), new { name = ProjectName() })),
            await StatusAsync(viewer.DeleteAsync(Uri($"{projects}/{project}"))),
            await StatusAsync(viewer.PostAsJsonAsync(Uri($"{projects}/{project}/databases"), new { name = "orders" })),
        ];
        var readProject = await GetAsync(viewer, $"{projects}/{project}");
        var listed = await GetAsync(viewer, projects);
        var databases = await GetAsync(viewer, $"{projects}/{project}/databases");
        // Fences need only membership; the denied commit did not advance the version.
        var fenced = await CommitAsync(viewer, new { op = "fence", organization, version });
        var members = await GetAsync(api.Client, $"{Organizations}/{organization}/members");

        await Assert.That(invited.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(invited.Value(0).GetProperty("role").GetString()).IsEqualTo("viewer");
        await Assert.That(invited.Value(0).GetProperty("status").GetString()).IsEqualTo("pending");
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accepted.Value(0).GetProperty("organizationId").GetString()).IsEqualTo(organization);
        await Assert.That(accepted.Value(0).GetProperty("member").GetProperty("userId").GetString()).IsEqualTo(viewerId);
        await Assert.That(accepted.Value(0).GetProperty("member").GetProperty("role").GetString()).IsEqualTo("viewer");
        await Assert.That((deniedProject.Status, deniedProject.Code, deniedProject.Op)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden", 1));
        await Assert.That((deniedDatabase.Status, deniedDatabase.Code, deniedDatabase.Op)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden", 0));
        await Assert.That(deniedRest).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Forbidden));
        await Assert.That(readProject.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Strings(listed.Json, "id")).IsEquivalentTo([project]);
        await Assert.That(databases.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Strings(databases.Json, "id")).IsEmpty();
        await Assert.That(fenced.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Roles(members.Json)).IsEquivalentTo([$"{api.User}=owner", $"{viewerId}=viewer"]);
    }

    [Test]
    public async Task Invitations_by_token_hash_are_looked_up_declined_resent_revoked_and_kept_in_history()
    {
        await using var api = await ManagementApi.StartAsync();
        var organizationName = OrganizationName();
        var organization = await api.CreateOrganizationAsync(organizationName);
        var inviteeId = User();
        using var invitee = api.As(inviteeId);
        var (declinedEmail, resentEmail, revokedEmail) = (Email(), Email(), Email());
        // The invitee proves an email only through its access token, whose verified email is compared ignoring case.
        using var asDeclined = api.As(inviteeId, declinedEmail.ToUpperInvariant());
        using var asResent = api.As(inviteeId, resentEmail);
        using var asRevoked = api.As(inviteeId, revokedEmail);
        var (declinedHash, oldHash, newHash, revokedHash) = (Hash(Token()), Hash(Token()), Hash(Token()), Hash(Token()));

        var invited = await CommitAsync(api.Client,
            new { op = "invite", organization, email = declinedEmail, role = "member", token_hash = declinedHash },
            new { op = "invite", organization, email = resentEmail, token_hash = oldHash },
            new { op = "invite", organization, email = revokedEmail, role = "admin", token_hash = revokedHash });
        var (declinedId, resentId, revokedId) = (Id(invited.Value(0)), Id(invited.Value(1)), Id(invited.Value(2)));
        var found = await PostAsync(invitee, Lookup, new { tokenHash = declinedHash });
        var declined = await CommitAsync(asDeclined, new { op = "decline_invitation", token_hash = declinedHash });
        var acceptDeclined = await CommitAsync(asDeclined, new { op = "accept_invitation", token_hash = declinedHash });
        var lookupDeclined = await PostAsync(invitee, Lookup, new { tokenHash = declinedHash });
        var resent = await CommitAsync(api.Client, new { op = "resend_invitation", organization, invitation = resentId, token_hash = newHash });
        var lookupOld = await PostAsync(invitee, Lookup, new { tokenHash = oldHash });
        var lookupNew = await PostAsync(invitee, Lookup, new { tokenHash = newHash });
        var acceptOld = await CommitAsync(asResent, new { op = "accept_invitation", token_hash = oldHash });
        var tokenTaken = await CommitAsync(api.Client, new { op = "invite", organization, email = Email(), token_hash = newHash });
        var stillPending = await CommitAsync(api.Client, new { op = "invite", organization, email = resentEmail, token_hash = Hash(Token()) });
        var revoked = await CommitAsync(api.Client, new { op = "revoke_invitation", organization, invitation = revokedId });
        var lookupRevoked = await PostAsync(invitee, Lookup, new { tokenHash = revokedHash });
        var acceptRevoked = await CommitAsync(asRevoked, new { op = "accept_invitation", token_hash = revokedHash });
        var unverified = await CommitAsync(invitee, new { op = "accept_invitation", token_hash = newHash });
        var accepted = await CommitAsync(asResent, new { op = "accept_invitation", token_hash = newHash });
        var history = await GetAsync(api.Client, $"{Organizations}/{organization}/invitations");

        await Assert.That(invited.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(found.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(found.Json.GetProperty("organization").GetProperty("id").GetString()).IsEqualTo(organization);
        await Assert.That(found.Json.GetProperty("organization").GetProperty("name").GetString()).IsEqualTo(organizationName);
        await Assert.That(Id(found.Json.GetProperty("invitation"))).IsEqualTo(declinedId);
        await Assert.That(declined.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(declined.Value(0).GetProperty("status").GetString()).IsEqualTo("declined");
        await Assert.That((acceptDeclined.Status, acceptDeclined.Code, acceptDeclined.Op)).IsEqualTo((HttpStatusCode.Conflict, "invitation_closed", 0));
        await Assert.That(lookupDeclined.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(resent.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Id(resent.Value(0))).IsEqualTo(resentId);
        await Assert.That(resent.Value(0).GetProperty("status").GetString()).IsEqualTo("pending");
        // Resending replaces the hash: the old one stops working.
        await Assert.That(lookupOld.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Id(lookupNew.Json.GetProperty("invitation"))).IsEqualTo(resentId);
        await Assert.That((acceptOld.Status, acceptOld.Code)).IsEqualTo((HttpStatusCode.NotFound, "not_found"));
        await Assert.That((tokenTaken.Status, tokenTaken.Code)).IsEqualTo((HttpStatusCode.Conflict, "token_taken"));
        await Assert.That((stillPending.Status, stillPending.Code)).IsEqualTo((HttpStatusCode.Conflict, "invitation_pending"));
        await Assert.That(revoked.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(revoked.Result(0).GetRawText()).IsEqualTo("{}");
        await Assert.That(lookupRevoked.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((acceptRevoked.Status, acceptRevoked.Code)).IsEqualTo((HttpStatusCode.Conflict, "invitation_closed"));
        // A token without a verified email cannot redeem: the body never names the email.
        await Assert.That((unverified.Status, unverified.Code, unverified.Op)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden", (int?)0));
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accepted.Value(0).GetProperty("member").GetProperty("userId").GetString()).IsEqualTo(inviteeId);
        await Assert.That(Statuses(history.Json)).IsEquivalentTo([$"{declinedId}=declined", $"{resentId}=accepted", $"{revokedId}=revoked"]);
    }

    [Test]
    public async Task Operations_on_an_organization_of_which_the_user_is_not_a_member_are_not_found()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var project = await api.CreateProjectAsync(organization, ProjectName());
        var version = await VersionAsync(api.Client, organization);
        using var stranger = api.As(User());

        Reply[] replies =
        [
            await CommitAsync(stranger, new { op = "create_project", organization, name = ProjectName() }),
            await CommitAsync(stranger, new { op = "fence", organization, version }),
            await CommitAsync(stranger, new { op = "create_database", organization, project, name = "orders" }),
            await CommitAsync(stranger, new { op = "rename_organization", organization, name = OrganizationName() }),
            await CommitAsync(stranger, new { op = "invite", organization, email = Email(), token_hash = Hash(Token()) }),
        ];
        // An organization created earlier in the commit does not open the other one.
        var mixed = await CommitAsync(stranger,
            new { op = "create_organization", name = OrganizationName() },
            new { op = "create_project", organization, name = ProjectName() });
        var versions = await GetAsync(stranger, $"{Versions}?organization={organization}");
        var strangerOrganizations = await GetAsync(stranger, Organizations);
        var projects = await GetAsync(api.Client, $"{Organizations}/{organization}/projects");

        await Assert.That(replies.Select(reply => (reply.Status, reply.Code, reply.Op)))
            .All().Satisfy(reply => reply.IsEqualTo((HttpStatusCode.NotFound, "not_found", (int?)0)));
        await Assert.That((mixed.Status, mixed.Code, mixed.Op)).IsEqualTo((HttpStatusCode.NotFound, "not_found", 1));
        await Assert.That(versions.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Strings(strangerOrganizations.Json, "id")).IsEmpty();
        await Assert.That(Strings(projects.Json, "id")).IsEquivalentTo([project]);
        await Assert.That(await VersionAsync(api.Client, organization)).IsEqualTo(version);
    }

    [Test]
    public async Task A_viewer_lists_members_takes_ownership_by_transfer_and_leaves()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var nominee = await api.AddMemberAsync(api.Client, organization, "viewer");
        var leaver = await api.AddMemberAsync(api.Client, organization, "viewer");
        using var nomineeClient = api.As(nominee);
        using var leaverClient = api.As(leaver);

        var members = await GetAsync(nomineeClient, $"{Organizations}/{organization}/members");
        var started = await CommitAsync(api.Client, new { op = "start_transfer", organization, user = nominee });
        var transfer = await GetAsync(nomineeClient, $"{Organizations}/{organization}/ownership-transfer");
        var confirmed = await CommitAsync(nomineeClient, new { op = "confirm_transfer", organization });
        var left = await CommitAsync(leaverClient, new { op = "leave", organization });
        var after = await GetAsync(nomineeClient, $"{Organizations}/{organization}/members");
        var audit = await GetAsync(nomineeClient, $"{Organizations}/{organization}/audit-events");
        var unknownLimit = await CommitAsync(api.Client, new { op = "limit", organization = NewId(), members = 5 });

        await Assert.That(members.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Roles(members.Json)).IsEquivalentTo([$"{api.User}=owner", $"{nominee}=viewer", $"{leaver}=viewer"]);
        await Assert.That(started.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transfer.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(confirmed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(left.Status).IsEqualTo(HttpStatusCode.OK);
        // Ownership moved and the starter became a member; the viewer who left is gone.
        await Assert.That(Roles(after.Json)).IsEquivalentTo([$"{api.User}=member", $"{nominee}=owner"]);
        // The audit log names the viewer role, never a writable one.
        await Assert.That(Items(audit.Json).Where(item => item.GetProperty("action").GetString() == "invitation.created").Select(item => item.GetProperty("detail").GetString()!))
            .IsEquivalentTo(["viewer", "viewer"]);
        // A limit on an organization that does not exist reads as one the user is not a member of.
        await Assert.That((unknownLimit.Status, unknownLimit.Code, unknownLimit.Op)).IsEqualTo((HttpStatusCode.NotFound, "not_found", (int?)0));
    }

    [Test]
    public async Task Owner_invitations_are_resent_only_by_owners_and_old_hashes_stay_taken()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var admin = await api.AddMemberAsync(api.Client, organization, "admin");
        using var adminClient = api.As(admin);
        var (first, second, third) = (Hash(Token()), Hash(Token()), Hash(Token()));
        var invited = await CommitAsync(api.Client, new { op = "invite", organization, email = Email(), role = "owner", token_hash = first });
        var invitation = Id(invited.Value(0));

        var byAdmin = await CommitAsync(adminClient, new { op = "resend_invitation", organization, invitation, token_hash = third });
        var byOwner = await CommitAsync(api.Client, new { op = "resend_invitation", organization, invitation, token_hash = second });
        // The first hash no longer names the invitation, and no other invitation can take it.
        var reused = await CommitAsync(api.Client, new { op = "invite", organization, email = Email(), token_hash = first });
        using var oldLookup = await api.Client.PostAsJsonAsync(Uri(Lookup), new { tokenHash = first });
        using var newLookup = await api.Client.PostAsJsonAsync(Uri(Lookup), new { tokenHash = second });

        await Assert.That((byAdmin.Status, byAdmin.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden"));
        await Assert.That(byOwner.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((reused.Status, reused.Code)).IsEqualTo((HttpStatusCode.Conflict, "token_taken"));
        await Assert.That(oldLookup.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(newLookup.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Numeric_roles_keep_their_meaning_from_before_viewer()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var member = await api.AddMemberAsync(api.Client, organization, "member");

        using var promoted = await api.Client.PutAsJsonAsync(Uri($"{Organizations}/{organization}/members/{member}/role"), new { role = 1 });
        var body = await promoted.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(promoted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("role").GetString()).IsEqualTo("admin");
    }

    [Test]
    public async Task Repeated_fences_compare_with_the_version_before_the_commit_and_read_only_commits_get_their_own_offset()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var versions = await GetAsync(api.Client, $"{Versions}?organization={organization}");
        var version = OrganizationVersion(versions.Json, organization)!;
        var account = versions.Json.GetProperty("account").GetString()!;

        var fenced = await CommitAsync(api.Client,
            new { op = "fence", organization, version },
            new { op = "fence", organization, version },
            new { op = "fence", account = true, version = account },
            new { op = "fence", account = true, version = account });
        var counted = await CommitAsync(api.Client, new { op = "limit", organization, members = 10 });
        var recorded = await GetAsync(api.Client, $"{Commits}/{counted.Id}");

        await Assert.That(fenced.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(counted.Status).IsEqualTo(HttpStatusCode.OK);
        // A commit of checks only still commits its record at an offset of its own, after the previous commit's.
        await Assert.That(long.Parse(counted.Json.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture))
            .IsGreaterThan(long.Parse(fenced.Json.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture));
        await Assert.That(recorded.Body).IsEqualTo(counted.Body);
    }

    [Test]
    public async Task Branches_need_a_source_that_exists_where_the_operation_runs()
    {
        await using var api = await ManagementApi.StartAsync();
        using var service = api.Service(api.User);
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var project = await api.CreateProjectAsync(organization, ProjectName());
        var database = await api.CreateDatabaseAsync(organization, project, "orders");
        var (newOrganization, newProject) = (NewId(), NewId());

        var inNewOrganization = await CommitAsync(service,
            new { op = "create_organization", id = newOrganization, name = OrganizationName() },
            new { op = "create_project", organization = newOrganization, id = newProject, name = ProjectName() },
            new { op = "branch_database", organization = newOrganization, project = newProject, database, name = "copy" });
        var afterDelete = await CommitAsync(api.Client,
            new { op = "delete_database", organization, project, database },
            new { op = "branch_database", organization, project, database, name = "copy" });
        var listed = await GetAsync(api.Client, $"{Organizations}/{organization}/projects/{project}/databases");
        var organizations = await GetAsync(api.Client, Organizations);

        await Assert.That((inNewOrganization.Status, inNewOrganization.Code, inNewOrganization.Op)).IsEqualTo((HttpStatusCode.NotFound, "not_found", (int?)2));
        await Assert.That((afterDelete.Status, afterDelete.Code, afterDelete.Op)).IsEqualTo((HttpStatusCode.NotFound, "not_found", (int?)1));
        await Assert.That(Strings(listed.Json, "id")).IsEquivalentTo([database]);
        await Assert.That(Strings(organizations.Json, "id")).IsEquivalentTo([organization]);
    }

    [Test]
    public async Task An_accepted_invitation_lets_the_same_commit_create_a_database()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var project = await api.CreateProjectAsync(organization, ProjectName());
        var (email, hash) = (Email(), Hash(Token()));
        await CommitAsync(api.Client, new { op = "invite", organization, email, token_hash = hash });
        var invitee = User();
        using var client = api.As(invitee, email);

        var joined = await CommitAsync(client,
            new { op = "accept_invitation", token_hash = hash },
            new { op = "create_database", organization, project, name = "orders" });
        var listed = await GetAsync(client, $"{Organizations}/{organization}/projects/{project}/databases");

        await Assert.That(joined.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Strings(listed.Json, "id")).IsEquivalentTo([Id(joined.Value(1))]);
    }

    [Test]
    public async Task Database_tooling_is_read_only_for_viewers_and_its_changes_advance_the_version()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(OrganizationName());
        var project = await api.CreateProjectAsync(organization, ProjectName());
        var database = await api.CreateDatabaseAsync(organization, project, "orders");
        var viewerId = await api.AddMemberAsync(api.Client, organization, "viewer");
        using var viewer = api.As(viewerId);
        var projectPath = $"{Organizations}/{organization}/projects/{project}";
        var databasePath = $"{projectPath}/databases/{database}";

        List<HttpStatusCode> denied =
        [
            await StatusAsync(viewer.PostAsJsonAsync(Uri($"{projectPath}/saved-queries"), new { name = "all", sql = "SELECT * FROM orders" })),
            await StatusAsync(viewer.PostAsJsonAsync(Uri($"{databasePath}/indexes"), new { collection = "orders", fields = StatusField })),
            await StatusAsync(viewer.PutAsJsonAsync(Uri($"{databasePath}/backup-policy"), new { retentionDays = 3 })),
            await StatusAsync(viewer.PostAsync(Uri($"{databasePath}/backups"), null)),
        ];
        List<HttpStatusCode> read =
        [
            await StatusAsync(viewer.GetAsync(Uri($"{projectPath}/saved-queries"))),
            await StatusAsync(viewer.GetAsync(Uri($"{databasePath}/indexes"))),
            await StatusAsync(viewer.GetAsync(Uri($"{databasePath}/backup-policy"))),
        ];
        var before = await VersionAsync(api.Client, organization);
        using var saved = await api.Client.PostAsJsonAsync(Uri($"{projectPath}/saved-queries"), new { name = "all", sql = "SELECT * FROM orders" });
        var after = await VersionAsync(api.Client, organization);

        await Assert.That(denied).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Forbidden));
        await Assert.That(read).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.OK));
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Created);
        // Saved queries are organization state the dashboard fences: changing one advances the version.
        await Assert.That(after).IsNotEqualTo(before);
    }

    [Test]
    public async Task Auto_index_policy_changes_advance_the_organization_version_but_repeats_do_not()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var policy = Uri($"/v1/projects/{database.Project}/databases/{database.Name}/auto-index/people");
        using var policyClient = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read databases:write";
        }));
        var before = await VersionAsync(api.Client, database.Organization);

        using var defaultPolicy = await policyClient.PutAsJsonAsync(policy, new { enabled = true });
        defaultPolicy.EnsureSuccessStatusCode();
        await Assert.That(await VersionAsync(api.Client, database.Organization)).IsEqualTo(before);
        using var disabled = await policyClient.PutAsJsonAsync(policy, new { enabled = false });
        disabled.EnsureSuccessStatusCode();
        var changed = await VersionAsync(api.Client, database.Organization);
        await Assert.That(changed).IsNotEqualTo(before);
        using var repeated = await policyClient.PutAsJsonAsync(policy, new { enabled = false });
        repeated.EnsureSuccessStatusCode();
        await Assert.That(await VersionAsync(api.Client, database.Organization)).IsEqualTo(changed);
        using var enabled = await policyClient.PutAsJsonAsync(policy, new { enabled = true });
        enabled.EnsureSuccessStatusCode();
        await Assert.That(await VersionAsync(api.Client, database.Organization)).IsNotEqualTo(changed);
    }

    [Test]
    public async Task Project_ids_stay_reserved_across_a_catalog_restart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-catalog-").FullName;
        try
        {
            string organization;
            string project;
            string version;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                organization = await api.CreateOrganizationAsync(OrganizationName());
                project = await api.CreateProjectAsync(organization, ProjectName());
                var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
                await using var transaction = await system.BeginTransactionAsync(write: false, CancellationToken.None);
                var marker = await transaction.GetAsync("catalog", "version", CancellationToken.None);
                version = Encoding.UTF8.GetString(marker!.Json.Span);
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            using var stranger = restarted.Service(User());
            // Another user's organization cannot take the existing project's id.
            var created = await CommitAsync(stranger, new { op = "create_organization", name = OrganizationName() });
            var strangerOrganization = Id(created.Value(0));
            var taken = await CommitAsync(stranger, new { op = "create_project", organization = strangerOrganization, id = project, name = ProjectName() });
            var system2 = await restarted.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var read = await system2.BeginTransactionAsync(write: false, CancellationToken.None);
            var markerAfterRestart = await read.GetAsync("catalog", "version", CancellationToken.None);
            var projectOrganization = await read.GetAsync("project_organizations", project, CancellationToken.None);

            await Assert.That(created.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((taken.Status, taken.Code, taken.Op)).IsEqualTo((HttpStatusCode.Conflict, "id_taken", (int?)0));
            await Assert.That(Encoding.UTF8.GetString(markerAfterRestart!.Json.Span)).IsEqualTo(version);
            await Assert.That(Encoding.UTF8.GetString(projectOrganization!.Json.Span)).IsEqualTo(JsonSerializer.Serialize(organization));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

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

    // The version of the document written by operation index of a commit.
    private static string Version(Reply commit, int index) => commit.Result(index).GetProperty("version").GetString()!;

    private static string? OrganizationVersion(JsonElement versions, string organization) =>
        versions.GetProperty("organizations").GetProperty(organization).GetString();

    private static async Task<string> VersionAsync(HttpClient client, string organization)
    {
        var versions = await GetAsync(client, $"{Versions}?organization={organization}");
        return OrganizationVersion(versions.Json, organization)!;
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

    private static async Task<Reply> PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(Uri(path), body);
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

    private static IEnumerable<string> Strings(JsonElement page, string property) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty(property).GetString()!);

    // "userId=role" of every member on a page.
    private static IEnumerable<string> Roles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(member => $"{member.GetProperty("userId").GetString()}={member.GetProperty("role").GetString()}");

    // "id=status" of every invitation on a page.
    private static IEnumerable<string> Statuses(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(invitation => $"{Id(invitation)}={invitation.GetProperty("status").GetString()}");

    /// <summary>A response read to the end; <see cref="Id"/> is the commit id that was sent, if any.</summary>
    private sealed record Reply(HttpStatusCode Status, string Body, JsonElement Json, string? ETag, bool Replayed, string? Id)
    {
        public string? Code => Error is { } error ? error.GetProperty("code").GetString() : null;

        public string? Message => Error is { } error ? error.GetProperty("message").GetString() : null;

        public int? Op => Error is { } error && error.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.Number ? op.GetInt32() : null;

        private JsonElement? Error => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error) ? error : null;

        public JsonElement Result(int index) => Json.GetProperty("results")[index];

        public JsonElement Value(int index) => Result(index).GetProperty("value");
    }
}
