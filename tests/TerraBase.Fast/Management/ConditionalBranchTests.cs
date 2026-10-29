using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ConditionalBranchTests
{
    [Test]
    public async Task Matching_source_creates_an_independent_child_with_the_admitted_lineage()
    {
        var hook = new UsageCommitHook();
        string? copiedSource = null;
        hook.BeforeCopy = (source, _) => { copiedSource = source.DatabaseId; return ValueTask.CompletedTask; };
        await using var api = await ManagementApi.StartAsync(services: hook.Install);
        var source = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, source.Project);
        await ManagementApi.CreateCollectionAsync(client, source, "people");
        var name = new Faker().Name.FullName();
        using (var seeded = await client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(source, "people", "saved")), new { name }))
        {
            seeded.EnsureSuccessStatusCode();
        }

        using var response = await client.PostAsJsonAsync(Route(source, source.Id), new { name = "preview" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
        var child = await response.Content.ReadFromJsonAsync<JsonElement>();
        var childId = child.GetProperty("id").GetString()!;
        await Assert.That(EntityIds.IsValid(childId)).IsTrue();
        await Assert.That(childId).IsNotEqualTo(source.Id);
        await Assert.That(child.GetProperty("branchedFrom").GetString()).IsEqualTo(source.Id);
        await Assert.That(copiedSource).IsEqualTo(source.Id);
        var preview = source with { Id = childId, Name = "preview" };
        await Assert.That(response.Headers.Location?.OriginalString).IsEqualTo(preview.Data);
        await Assert.That(await ReadNameAsync(client, preview)).IsEqualTo(name);
        using (var changed = await client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(preview, "people", "saved")), new { name = "Changed" }))
        {
            changed.EnsureSuccessStatusCode();
        }
        await Assert.That(await ReadNameAsync(client, source)).IsEqualTo(name);
        var original = await client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(source.Data));
        await Assert.That(original.GetProperty("id").GetString()).IsEqualTo(source.Id);
        await Assert.That(original.GetProperty("branchedFrom").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    [Arguments("malformed")]
    [Arguments("lowercase")]
    [Arguments("overflow")]
    [Arguments("mismatch")]
    [Arguments("replacement")]
    public async Task Refused_identity_does_not_prepare_storage_or_copy_a_database(string scenario)
    {
        var copies = 0;
        var preparations = 0;
        var placementReads = 0;
        var hook = new UsageCommitHook
        {
            BeforeCopy = (_, _) => { Interlocked.Increment(ref copies); return ValueTask.CompletedTask; },
        };
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            hook.Install(services);
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => UsageApiTests.SlowManagement.Wrap(
                (IManagementService)registration.ImplementationFactory!(provider), operation =>
                {
                    if (operation == nameof(IManagementService.GetDatabaseAsync)) { Interlocked.Increment(ref placementReads); }
                }));
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == "create.prepare") { Interlocked.Increment(ref preparations); }
            });
        });
        var source = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, source.Project);
        if (scenario == "replacement")
        {
            using var deleted = await client.DeleteAsync(ManagementApi.Uri(source.Data));
            deleted.EnsureSuccessStatusCode();
            _ = await api.CreateDatabaseAsync(source.Organization, source.Project, source.Name);
        }
        var expected = scenario switch
        {
            "malformed" => "not-a-ulid",
            "lowercase" => source.Id.ToLowerInvariant(),
            "overflow" => "80000000000000000000000000",
            "replacement" => source.Id,
            _ => EntityIds.New().ToString(),
        };
        var valid = scenario is "mismatch" or "replacement";
        var before = Volatile.Read(ref preparations);
        var readsBefore = Volatile.Read(ref placementReads);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var storageBefore = await StoredIdsAsync(engine);
        await ErrorAssert.ErrorAsync(client.PostAsJsonAsync(Route(source, expected), new { name = "preview" }),
            valid ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, valid ? "database_identity_changed" : "invalid_request",
            valid ? "the database identity changed" : "expectedId must be a canonical uppercase ULID");
        await Assert.That(Volatile.Read(ref copies)).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref placementReads)).IsEqualTo(readsBefore);
        await Assert.That(Volatile.Read(ref preparations)).IsEqualTo(before);
        await Assert.That(await StoredIdsAsync(engine)).IsEquivalentTo(storageBefore);
        var listed = await client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri($"/v1/projects/{source.Project}/databases"));
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed.GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo(source.Name);
    }

    [Test]
    [Arguments("other-project", 404)]
    [Arguments("other-source", 404)]
    [Arguments("source-only", 404)]
    [Arguments("read-only", 403)]
    [Arguments("anonymous", 401)]
    public async Task Authorization_refuses_before_disclosing_the_identity_guard(string restriction, int status)
    {
        await using var api = await ManagementApi.StartAsync();
        var source = await api.CreateManagedDatabaseAsync();
        using var client = restriction == "anonymous" ? api.WithToken(null)
            : ProjectClient(api, restriction == "other-project" ? EntityIds.New().ToString() : source.Project,
                restriction == "other-source" ? EntityIds.New().ToString() : restriction == "source-only" ? source.Id : null,
                write: restriction != "read-only");
        using var response = await client.PostAsJsonAsync(Route(source, EntityIds.New().ToString()), new { name = "preview" });
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
        await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("database_identity_changed");
    }

    [Test]
    public async Task Replacing_the_name_after_copy_admission_cannot_redirect_the_storage_source()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(services: hook.Install);
        var source = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, source.Project);
        using var gate = new UsageCommitHook.Gate(fail: false);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var expectedStorage = await StoredIdsAsync(engine);
        var copies = 0;
        string? copiedSource = null;
        hook.BeforeCopy = async (storage, cancellationToken) =>
        {
            Interlocked.Increment(ref copies);
            copiedSource = storage.DatabaseId;
            await gate.WaitAsync(cancellationToken);
        };
        var pending = client.PostAsJsonAsync(Route(source, source.Id), new { name = "preview" });
        try
        {
            await gate.Entered.Task.WaitAsync(TestContext.Current!.Execution.CancellationToken);
            using var deleted = await client.DeleteAsync(ManagementApi.Uri(source.Data));
            deleted.EnsureSuccessStatusCode();
            var replacement = await api.CreateDatabaseAsync(source.Organization, source.Project, source.Name);
            await Assert.That(replacement).IsNotEqualTo(source.Id);
            expectedStorage.Remove(source.Id);
            expectedStorage.Add(replacement);
        }
        finally
        {
            gate.Dispose();
        }
        await ErrorAssert.ErrorAsync(pending, HttpStatusCode.NotFound, "unknown_route");
        await Assert.That(copiedSource).IsEqualTo(source.Id);
        await Assert.That(Volatile.Read(ref copies)).IsEqualTo(1);
        await Assert.That(await StoredIdsAsync(engine)).IsEquivalentTo(expectedStorage);
        var listed = await client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri($"/v1/projects/{source.Project}/databases"));
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed.GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo(source.Name);
    }

    private static HttpClient ProjectClient(ManagementApi api, string project, string? database = null, bool write = true) =>
        api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = write ? "databases:read databases:write documents:read documents:write" : "databases:read";
            if (database is not null) { token.Claims["database"] = database; }
        }));

    private static Uri Route(ManagedDatabaseRef source, string expected) => ManagementApi.Uri(source.Data + "/branches/if-id/" + expected);

    private static async Task<string?> ReadNameAsync(HttpClient client, ManagedDatabaseRef database) =>
        (await client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "saved"))))
            .GetProperty("document").GetProperty("name").GetString();

    private static async Task<List<string>> StoredIdsAsync(IStorageEngine engine)
    {
        List<string> ids = [];
        await foreach (var database in engine.ListDatabasesAsync(TestContext.Current!.Execution.CancellationToken))
        {
            ids.Add(database.DatabaseId!);
        }
        return ids;
    }
}
