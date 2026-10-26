using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class DatabaseIdentityDeletionTests
{
    private const string Scopes = "databases:read databases:write documents:read documents:write";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Matching_identity_deletes_and_unconditional_delete_still_works(bool conditional)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        using var deleted = await client.DeleteAsync(Uri(database.Data + (conditional ? "/if-id/" + database.Id : "")));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync(Uri(database.Data));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Replicated_and_all_regions_deletion_records_purge_without_mutating_placement(bool allRegions)
    {
        var placementChanges = 0;
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            new OtherRegions("sjc", "fra").Register(services);
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => UsageApiTests.SlowManagement.Wrap(
                (IManagementService)registration.ImplementationFactory!(provider), operation =>
                {
                    if (operation == nameof(IManagementService.SetDatabaseRegionsAsync))
                    {
                        Interlocked.Increment(ref placementChanges);
                    }
                }));
        });
        var organization = await api.CreateOrganizationAsync("Replicated deletion");
        var project = await api.CreateProjectAsync(organization, "Project");
        await api.VerifyProjectBindingAsync(organization, project);
        using var client = ProjectClient(api, project);
        object placement = allRegions ? new { name = "main", allRegions = true }
            : new { name = "main", replicaRegions = new[] { "sjc", "fra" } };
        using var created = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), placement);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("allRegions").GetBoolean()).IsEqualTo(allRegions);
        if (!allRegions)
        {
            await Assert.That(body.GetProperty("replicaRegions").GetArrayLength()).IsEqualTo(2);
        }

        var database = new ManagedDatabaseRef(organization, project, body.GetProperty("id").GetString()!, "main");
        await ErrorAssert.ErrorAsync(client.DeleteAsync(Uri(database.Data + "/if-id/" + EntityIds.New())), HttpStatusCode.Conflict, "database_identity_mismatch");
        using var deleted = await client.DeleteAsync(Uri(database.Data + "/if-id/" + database.Id));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
        await Assert.That(Volatile.Read(ref placementChanges)).IsEqualTo(0);
        var purges = await api.Server.Services.GetRequiredService<SystemCatalog>().ListDatabasePurgesAsync(null, 100, CancellationToken.None);
        await Assert.That(purges!.Items.Select(record => record.Database.DatabaseId!)).IsEquivalentTo([database.Id]);
    }

    [Test]
    public async Task Mismatched_identity_refuses_before_placement_or_delete_and_preserves_documents()
    {
        var armed = false;
        var calls = 0;
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => UsageApiTests.SlowManagement.Wrap(
                (IManagementService)registration.ImplementationFactory!(provider), operation =>
                {
                    if (Volatile.Read(ref armed) && operation is nameof(IManagementService.GetDatabaseAsync) or nameof(IManagementService.DeleteDatabaseAsync))
                    {
                        Interlocked.Increment(ref calls);
                    }
                }));
        });
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        var document = await PutDocumentAsync(client, database);
        Volatile.Write(ref armed, true);
        await ErrorAssert.ErrorAsync(client.DeleteAsync(Uri(database.Data + "/if-id/" + EntityIds.New())),
            HttpStatusCode.Conflict, "database_identity_mismatch", "the database identity does not match expectedId");
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
        Volatile.Write(ref armed, false);
        await AssertDocumentAsync(client, document);
    }

    [Test]
    [Arguments("not-an-id")]
    [Arguments("01arz3ndektsv4rrffq69g5fav")]
    [Arguments("81ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAI")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAV,01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    public async Task Malformed_identity_is_a_safe_bad_request(string expected)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        using var response = await client.DeleteAsync(Uri(database.Data + "/if-id/" + expected));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_request");
        await Assert.That(body.GetProperty("error").GetProperty("message").GetString()).DoesNotContain(expected);
        using var preserved = await client.GetAsync(Uri(database.Data));
        await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Recreated_name_does_not_satisfy_the_old_identity()
    {
        await using var api = await ManagementApi.StartAsync();
        var original = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(original.Organization, original.Project);
        using var client = ProjectClient(api, original.Project);
        using (var deleted = await client.DeleteAsync(Uri(original.Data + "/if-id/" + original.Id)))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        var replacement = await api.CreateManagedDatabaseAsync(original.Organization, original.Project, original.Name);
        await Assert.That(replacement.Id).IsNotEqualTo(original.Id);
        var document = await PutDocumentAsync(client, replacement);
        await ErrorAssert.ErrorAsync(client.DeleteAsync(Uri(original.Data + "/if-id/" + original.Id)), HttpStatusCode.Conflict, "database_identity_mismatch");
        await AssertDocumentAsync(client, document);
    }

    [Test]
    public async Task Name_replacement_after_resolution_cannot_redirect_the_delete_to_the_new_database()
    {
        ManagementApi? fixture = null;
        ManagedDatabaseRef? original = null;
        IManagementService? inner = null;
        string? replacementDocument = null;
        var armed = 0;
        var resolutions = 0;
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider =>
            {
                inner = (IManagementService)registration.ImplementationFactory!(provider);
                return UsageApiTests.SlowManagement.Wrap(inner, operation =>
                {
                    if (Volatile.Read(ref armed) == 1 && operation == nameof(IManagementService.FindDatabaseRecordAsync))
                    {
                        Interlocked.Increment(ref resolutions);
                    }
                }, operation => operation == nameof(IManagementService.GetDatabaseAsync) && Interlocked.Exchange(ref armed, 0) == 1
                    ? ReplaceBeforePlacementAsync() : null);
            });
        });
        fixture = api;
        original = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(original.Organization, original.Project);
        using var client = ProjectClient(api, original.Project);
        Volatile.Write(ref armed, 1);
        using var stale = await client.DeleteAsync(Uri(original.Data + "/if-id/" + original.Id));
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Volatile.Read(ref resolutions)).IsEqualTo(1);
        await AssertDocumentAsync(client, replacementDocument!);

        async ValueTask<GetDatabaseResult> ReplaceBeforePlacementAsync()
        {
            using var deleted = await fixture!.Client.DeleteAsync(Uri(original!.Path));
            deleted.EnsureSuccessStatusCode();
            var replacement = await fixture.CreateManagedDatabaseAsync(original.Organization, original.Project, original.Name);
            await Assert.That(replacement.Id).IsNotEqualTo(original.Id);
            using var writer = ProjectClient(fixture, original.Project);
            replacementDocument = await PutDocumentAsync(writer, replacement);
            return await inner!.GetDatabaseAsync(fixture.User, original.Organization, original.Project, original.Id, CancellationToken.None);
        }
    }

    [Test]
    public async Task Expected_identity_does_not_override_project_database_or_scope_authorization()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var second = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "other");
        using var allowed = ProjectClient(api, database.Project);
        var document = await PutDocumentAsync(allowed, database);
        using var anonymous = api.WithToken(null);
        using var readOnly = ProjectClient(api, database.Project, "databases:read");
        using var wrongProject = ProjectClient(api, EntityIds.New().ToString());
        using var restricted = ProjectClient(api, database.Project, database: second.Name);
        foreach (var (client, status) in new[]
        {
            (anonymous, HttpStatusCode.Unauthorized), (readOnly, HttpStatusCode.Forbidden),
            (wrongProject, HttpStatusCode.NotFound), (restricted, HttpStatusCode.NotFound),
        })
        {
            using var response = await client.DeleteAsync(Uri(database.Data + "/if-id/" + database.Id));
            await Assert.That(response.StatusCode).IsEqualTo(status);
        }

        // A matching ID from another database is a conflict, never authority to delete that database.
        await ErrorAssert.ErrorAsync(allowed.DeleteAsync(Uri(database.Data + "/if-id/" + second.Id)), HttpStatusCode.Conflict, "database_identity_mismatch");
        using var secondStillExists = await allowed.GetAsync(Uri(second.Data));
        await Assert.That(secondStillExists.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await AssertDocumentAsync(allowed, document);
    }

    [Test]
    public async Task Openapi_documents_required_identity_path_and_failure_responses()
    {
        await using var api = await ManagementApi.StartAsync();
        using var client = api.WithToken(null);
        var document = await client.GetFromJsonAsync<JsonElement>(Uri("/openapi/v1.json"));
        var operation = document.GetProperty("paths").GetProperty("/v1/projects/{project}/databases/{database}/if-id/{expectedId}").GetProperty("delete");
        var identity = operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "expectedId");
        await Assert.That(identity.GetProperty("in").GetString()).IsEqualTo("path");
        await Assert.That(identity.GetProperty("required").GetBoolean()).IsTrue();
        await Assert.That(identity.GetProperty("schema").GetProperty("pattern").GetString()).IsEqualTo("^[0-7][0-9A-HJKMNP-TV-Z]{25}$");
        foreach (var status in new[] { "204", "400", "403", "404", "409", "421", "503" })
        {
            await Assert.That(operation.GetProperty("responses").TryGetProperty(status, out _)).IsTrue();
        }
    }

    private static HttpClient ProjectClient(ManagementApi api, string project, string scopes = Scopes, string? database = null) =>
        api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = scopes;
            if (database is not null)
            {
                token.Claims["database"] = database;
            }
        }));

    private static async Task<string> PutDocumentAsync(HttpClient client, ManagedDatabaseRef database)
    {
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var path = ManagementApi.DocumentPath(database, "people", "preserved");
        using var written = await client.PutAsJsonAsync(Uri(path), new { value = "preserved" });
        written.EnsureSuccessStatusCode();
        return path;
    }

    private static async Task AssertDocumentAsync(HttpClient client, string path)
    {
        var document = await client.GetFromJsonAsync<JsonElement>(Uri(path));
        await Assert.That(document.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("preserved");
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
