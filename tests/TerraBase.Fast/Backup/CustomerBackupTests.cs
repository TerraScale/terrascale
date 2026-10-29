using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupTests
{
    [Test]
    public async Task Both_destinations_ship_encrypted_snapshots_and_wal_and_restore_actual_documents()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "both");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var after = faker.Lorem.Sentence(12);
        await PutAsync(data, database, "before", before);
        await ShipAsync(api, time);
        var snapshot = time.GetUtcNow();
        await PutAsync(data, database, "after", after);
        await ShipAsync(api, time);

        foreach (var destination in new[] { "platform", "customer" })
        {
            using var window = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time?destination={destination}"), cancellationToken);
            window.EnsureSuccessStatusCode();
            var bounds = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            await Assert.That(bounds.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(snapshot);
            var latest = bounds.GetProperty("latest").GetDateTimeOffset();
            var restored = await RestoreAsync(api, database, latest, "latest-" + destination, destination);
            await AssertDocumentAsync(data, restored, "before", before);
            await AssertDocumentAsync(data, restored, "after", after);
            var earlier = await RestoreAsync(api, database, snapshot, "earlier-" + destination, destination);
            await AssertDocumentAsync(data, earlier, "before", before);
            using var absent = await data.GetAsync(Uri(ManagementApi.DocumentPath(earlier, "people", "after")), cancellationToken);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        foreach (var destination in new string?[] { null, "", "platform" })
        {
            var query = destination is null ? "" : "?destination=" + destination;
            using var defaultWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time" + query), cancellationToken);
            defaultWindow.EnsureSuccessStatusCode();
            var defaultBounds = await defaultWindow.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var defaultRestore = await RestoreAsync(api, database, defaultBounds.GetProperty("latest").GetDateTimeOffset(),
                "default-" + (destination is null ? "omitted" : destination.Length == 0 ? "empty" : destination), destination);
            await AssertDocumentAsync(data, defaultRestore, "after", after);
        }
        var customerKeys = s3.CustomerObjects.Keys.ToArray();
        await Assert.That(customerKeys.Any(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.Any(key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.All(s3.Objects.ContainsKey)).IsTrue();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith("v1/_system/nodes/local/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.Any(key => key.Contains("_system", StringComparison.Ordinal))).IsFalse();
        await Assert.That(s3.CustomerObjects.All(item => item.Value.AsSpan().StartsWith("TBK1"u8) || item.Key.StartsWith("v1/keys/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.CustomerObjects.Values.Any(content => content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(after)) >= 0)).IsFalse();
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments("customer")]
    [Arguments("both")]
    public async Task Project_tokens_select_platform_or_customer_plans_and_restore_only_within_each_stored_window(string mode)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var writer = ProjectClient(api, database.Project, "databases:read databases:write documents:write");
        using var reader = ProjectClient(api, database.Project, "databases:read", database.Id);
        await ManagementApi.CreateCollectionAsync(writer, database, "before");
        await ShipAsync(api, time);
        var platformSnapshot = time.GetUtcNow();
        await SetAsync(api, database, mode);
        await ManagementApi.CreateCollectionAsync(writer, database, "snapshot");
        await ShipAsync(api, time);
        var customerSnapshot = time.GetUtcNow();
        await ManagementApi.CreateCollectionAsync(writer, database, "after");
        await ShipAsync(api, time);
        var customerLatest = time.GetUtcNow();
        // A maintenance-only pass must not extend either source beyond its newest stored snapshot or WAL.
        await ShipAsync(api, time);
        var planRoute = Uri(database.Data + "/point-in-time/plan");
        var restoreRoute = Uri(database.Data + "/point-in-time/restore");

        foreach (var destination in new[] { "platform", "customer" })
        {
            using var window = await reader.GetAsync(Uri(database.Data + "/point-in-time?destination=" + destination), cancellationToken);
            window.EnsureSuccessStatusCode();
            var bounds = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var latest = bounds.GetProperty("latest").GetDateTimeOffset();
            var mirrored = destination == "customer" || mode == "both";
            await Assert.That(bounds.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(destination == "customer" ? customerSnapshot : platformSnapshot);
            await Assert.That(latest).IsEqualTo(mirrored ? customerLatest : platformSnapshot);
            await Assert.That(latest).IsLessThan(time.GetUtcNow());
            using var preview = await reader.PostAsJsonAsync(planRoute, new { pointInTime = latest, destination }, cancellationToken);
            preview.EnsureSuccessStatusCode();
            var plan = await preview.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            await Assert.That(plan.GetProperty("window").GetRawText()).IsEqualTo(bounds.GetRawText());
            await Assert.That(plan.GetProperty("pointInTime").GetDateTimeOffset()).IsEqualTo(latest);
            await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(mirrored ? customerSnapshot : platformSnapshot);
            await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(mirrored ? 1 : 0);
            await Assert.That(plan.GetProperty("latestSegmentAt").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : plan.GetProperty("latestSegmentAt").GetDateTimeOffset())
                .IsEqualTo(mirrored ? customerLatest : null);

            foreach (var at in new[] { latest.AddTicks(1), time.GetUtcNow() })
            {
                // Not in the future: the backup does not hold the time yet, for the plan as for the restore.
                await ErrorAssert.ErrorAsync(reader.PostAsJsonAsync(planRoute, new { pointInTime = at, destination }, cancellationToken),
                    HttpStatusCode.Conflict, "backup_history_incomplete", "continuous backup cannot prove this database's history complete up to pointInTime");
                await ErrorAssert.ErrorAsync(writer.PostAsJsonAsync(restoreRoute, new { pointInTime = at, destination, name = "after-" + destination }, cancellationToken),
                    HttpStatusCode.Conflict, "backup_history_incomplete", "continuous backup cannot prove this database's history complete up to pointInTime");
            }

            var name = "selected-" + destination;
            using var restored = await writer.PostAsJsonAsync(restoreRoute, new { pointInTime = latest, destination, name }, cancellationToken);
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(restored.Headers.Location!.OriginalString).IsEqualTo($"/v1/projects/{database.Project}/databases/{name}");
            await Assert.That((await restored.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
            var schema = await writer.GetFromJsonAsync<JsonElement>(Uri($"/v1/projects/{database.Project}/databases/{name}/schema"), cancellationToken: cancellationToken);
            string?[] expectedCollections = mirrored ? ["before", "snapshot", "after"] : ["before"];
            await Assert.That(schema.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()))
                .IsEquivalentTo(expectedCollections);
        }

        foreach (var destination in new string?[] { null, "", "platform" })
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal) { ["pointInTime"] = platformSnapshot };
            if (destination is not null)
            {
                body["destination"] = destination;
            }

            using var preview = await reader.PostAsJsonAsync(planRoute, body, cancellationToken);
            preview.EnsureSuccessStatusCode();
            var plan = await preview.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(platformSnapshot);
            await Assert.That(plan.GetProperty("window").GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(platformSnapshot);
            await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(0);
        }

        // Before the customer bucket's oldest snapshot, within the retention window: it does not hold the time.
        await ErrorAssert.ErrorAsync(reader.PostAsJsonAsync(planRoute, new { pointInTime = platformSnapshot, destination = "customer" }, cancellationToken),
            HttpStatusCode.Conflict, "backup_history_incomplete", "continuous backup cannot prove this database's history complete up to pointInTime");
        using var historical = await reader.PostAsJsonAsync(planRoute, new { pointInTime = customerSnapshot, destination = "customer" }, cancellationToken);
        historical.EnsureSuccessStatusCode();
        var earlier = await historical.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(earlier.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(customerSnapshot);
        await Assert.That(earlier.GetProperty("segmentCount").GetInt32()).IsEqualTo(0);
        await Assert.That(earlier.GetProperty("latestSegmentAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await ErrorAssert.ErrorAsync(reader.PostAsJsonAsync(planRoute, new { pointInTime = customerLatest, destination = "Customer" }, cancellationToken),
            HttpStatusCode.BadRequest, "invalid_request", "destination must be platform or customer");
        using var narrowed = ProjectClient(api, database.Project, "databases:read databases:write", Ulid.NewUlid().ToString());
        using var hiddenWindow = await narrowed.GetAsync(Uri(database.Data + "/point-in-time?destination=customer"), cancellationToken);
        using var hiddenPlan = await narrowed.PostAsJsonAsync(planRoute, new { }, cancellationToken);
        using var hiddenRestore = await narrowed.PostAsJsonAsync(restoreRoute, new { }, cancellationToken);
        foreach (var response in new[] { hiddenWindow, hiddenPlan, hiddenRestore })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Project_destination_configuration_requires_scope_and_hides_other_projects_tenants_and_database_narrowed_tokens()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, s3.Route, time);
        var database = await api.CreateManagedDatabaseAsync();
        var siblingProject = await api.CreateProjectAsync(database.Organization, "Destination sibling");
        var sibling = await api.CreateManagedDatabaseAsync(database.Organization, siblingProject, "main");
        var otherTenant = await api.CreateManagedDatabaseAsync();

        var route = Uri($"/v1/projects/{database.Project}/backup-destination");
        using var writer = ProjectClient(api, database.Project, "databases:write");
        using var reader = ProjectClient(api, database.Project, "databases:read");
        using var saved = await writer.PutAsJsonAsync(route, Destination("customer"), cancellationToken);
        saved.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await saved.Content.ReadAsStringAsync(cancellationToken), "customer");
        await SetAsync(api, sibling, "both");
        await SetAsync(api, otherTenant, "both");
        var configured = await reader.GetStringAsync(route, cancellationToken);
        await AssertDestinationAsync(configured, "customer");
        using var writeOnlyGet = await writer.GetAsync(route, cancellationToken);
        using var readOnlyPut = await reader.PutAsJsonAsync(route, Destination("both"), cancellationToken);
        using var readOnlyDelete = await reader.DeleteAsync(route, cancellationToken);
        foreach (var response in new[] { writeOnlyGet, readOnlyPut, readOnlyDelete })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        foreach (var (project, scope, name, status) in new (string, string, string?, HttpStatusCode)[]
        {
            (database.Project, "documents:read documents:write", null, HttpStatusCode.Forbidden),
            (sibling.Project, "databases:read databases:write", null, HttpStatusCode.NotFound),
            (otherTenant.Project, "databases:read databases:write", null, HttpStatusCode.NotFound),
            (database.Project, "databases:read databases:write", database.Id, HttpStatusCode.NotFound),
            (database.Project, "databases:read databases:write", Ulid.NewUlid().ToString(), HttpStatusCode.NotFound),
        })
        {
            using var client = ProjectClient(api, project, scope, name);
            using var get = await client.GetAsync(route, cancellationToken);
            using var put = await client.PutAsJsonAsync(route, Destination("both"), cancellationToken);
            using var delete = await client.DeleteAsync(route, cancellationToken);
            foreach (var response in new[] { get, put, delete })
            {
                await Assert.That(response.StatusCode).IsEqualTo(status);
            }
        }

        using var anonymous = api.WithToken(null);
        using var anonymousGet = await anonymous.GetAsync(route, cancellationToken);
        using var anonymousPut = await anonymous.PutAsJsonAsync(route, Destination("both"), cancellationToken);
        using var anonymousDelete = await anonymous.DeleteAsync(route, cancellationToken);
        foreach (var response in new[] { anonymousGet, anonymousPut, anonymousDelete })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await Assert.That(await reader.GetStringAsync(route, cancellationToken)).IsEqualTo(configured);
        using var removed = await writer.DeleteAsync(route, cancellationToken);
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var absent = await reader.GetAsync(route, cancellationToken);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var target in new[] { sibling, otherTenant })
        {
            using var client = ProjectClient(api, target.Project, "databases:read");
            await AssertDestinationAsync(await client.GetStringAsync(Uri($"/v1/projects/{target.Project}/backup-destination"), cancellationToken), "both");
        }

        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Customer_only_shipping_is_isolated_to_its_project_and_keeps_system_backups_on_platform()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var customer = await api.CreateManagedDatabaseAsync();
        var siblingProject = await api.CreateProjectAsync(customer.Organization, "Platform sibling");
        var sibling = await api.CreateManagedDatabaseAsync(customer.Organization, siblingProject, "main");
        var otherOrganization = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(customer.Organization, "documents:read", "documents:write");
        await SetAsync(api, customer, "customer");
        await ManagementApi.CreateCollectionAsync(data, customer, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, customer, "only", text);
        await ShipAsync(api, time);

        await Assert.That(s3.CustomerObjects.Keys.Any(key => key.StartsWith(File(customer) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(sibling) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(otherOrganization) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith("v1/_system/nodes/local/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s3.CustomerObjects.Keys.All(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal)
            || key.StartsWith($"v1/keys/{customer.Organization}/{customer.Project}/", StringComparison.Ordinal))).IsTrue();
        using var platformWindow = await api.Client.GetAsync(Uri(customer.Path + "/point-in-time"), cancellationToken);
        using var platformRestore = await api.Client.PostAsJsonAsync(Uri(customer.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), name = "no-platform" }, cancellationToken);
        using var siblingWindow = await api.Client.GetAsync(Uri(sibling.Path + "/point-in-time?destination=customer"), cancellationToken);
        await Assert.That(platformWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(platformRestore.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var emptyWindow = await api.Client.GetAsync(Uri(customer.Path + "/point-in-time?destination="), cancellationToken);
        using var emptyRestore = await api.Client.PostAsJsonAsync(Uri(customer.Path + "/point-in-time/restore"),
            new { pointInTime = time.GetUtcNow(), name = "no-platform-empty", destination = "" }, cancellationToken);
        await Assert.That(emptyWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(emptyRestore.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(siblingWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var restored = await RestoreAsync(api, customer, await LatestAsync(api.Client, customer, "customer"), "customer-restored", "customer");
        await AssertDocumentAsync(data, restored, "only", text);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Credentials_are_sealed_in_the_catalog_and_delete_resumes_platform_without_removing_customer_objects()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        using var unset = await api.Client.GetAsync(route, cancellationToken);
        await Assert.That(unset.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await SetAsync(api, database, "customer");
        using var get = await api.Client.GetAsync(route, cancellationToken);
        get.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await get.Content.ReadAsStringAsync(cancellationToken), "customer");
        var catalog = string.Join('\n', await DocumentFiles.ReadAsync(Path.Combine(api.Server.DataDirectory, "_system.db")));
        await Assert.That(catalog.Contains(FakeS3.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(catalog.Contains(FakeS3.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        foreach (var path in new[] { database.ProjectPath, database.Path, database.ProjectPath + "/databases" })
        {
            var response = await api.Client.GetStringAsync(Uri(path), cancellationToken);
            await AssertPublicAsync(response);
        }

        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        var retained = s3.CustomerObjects.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal);
        using var deleted = await api.Client.DeleteAsync(route, cancellationToken);
        using var gone = await api.Client.GetAsync(route, cancellationToken);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await ShipAsync(api, time);
        await Assert.That(s3.CustomerObjects.Keys).IsEquivalentTo(retained.Keys);
        await Assert.That(retained.All(item => s3.CustomerObjects[item.Key].AsSpan().SequenceEqual(item.Value))).IsTrue();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        using var customerWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        await Assert.That(customerWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database), "platform-resumed");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task An_empty_project_with_a_customer_backup_destination_requires_detach_before_deletion()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        var queryClock = new SteppingClock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, queryClock);
        }, time);
        var organization = await api.CreateOrganizationAsync(new Faker().Company.CompanyName());
        var project = await api.CreateProjectAsync(organization, "Empty project");
        var projectRoute = Uri($"/v1/management/organizations/{organization}/projects/{project}");
        var destinationRoute = Uri(projectRoute.OriginalString + "/backup-destination");
        using var saved = await api.Client.PutAsJsonAsync(destinationRoute, Destination("customer"), cancellationToken);
        saved.EnsureSuccessStatusCode();

        using var rejected = await DeleteProjectAsync();
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using var configured = await api.Client.GetAsync(destinationRoute, cancellationToken);
        configured.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await configured.Content.ReadAsStringAsync(cancellationToken), "customer");

        using var detached = await api.Client.DeleteAsync(destinationRoute, cancellationToken);
        await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var unconfigured = await api.Client.GetAsync(destinationRoute, cancellationToken);
        await Assert.That(unconfigured.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        queryClock.Armed = true;
        using var deleted = await DeleteProjectAsync();
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because(await deleted.Content.ReadAsStringAsync(cancellationToken));
        using var gone = await api.Client.GetAsync(projectRoute, cancellationToken);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);

        Task<HttpResponseMessage> DeleteProjectAsync()
        {
            return api.Client.DeleteAsync(projectRoute, cancellationToken);
        }
    }

    [Test]
    public async Task Changing_a_destination_starts_a_new_generation_and_stops_platform_shipping_in_customer_mode()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "both");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var after = faker.Lorem.Sentence(12);
        await PutAsync(data, database, "before", before);
        await ShipAsync(api, time);
        var platformLatest = time.GetUtcNow();
        var initial = Generations(s3.CustomerObjects, database);
        await Assert.That(initial.Count).IsEqualTo(1);
        var platformKeys = s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/", StringComparison.Ordinal)).ToArray();
        await SetAsync(api, database, "customer");
        await PutAsync(data, database, "after", after);
        await ShipAsync(api, time);
        var changed = Generations(s3.CustomerObjects, database);
        await Assert.That(changed.Count).IsEqualTo(2);
        await Assert.That(changed.IsSupersetOf(initial)).IsTrue();
        await Assert.That(s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/", StringComparison.Ordinal))).IsEquivalentTo(platformKeys);
        using var platformWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=platform"), cancellationToken);
        using var customerWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        platformWindow.EnsureSuccessStatusCode();
        customerWindow.EnsureSuccessStatusCode();
        await Assert.That((await platformWindow.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset()).IsEqualTo(platformLatest);
        var customerLatest = (await customerWindow.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
        await Assert.That(customerLatest).IsEqualTo(time.GetUtcNow());
        var restored = await RestoreAsync(api, database, customerLatest, "changed-destination", "customer");
        await AssertDocumentAsync(data, restored, "before", before);
        await AssertDocumentAsync(data, restored, "after", after);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Invalid_destinations_and_unauthorized_callers_cannot_change_or_restore_customer_backups()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, s3.Route, time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        var valid = Destination("both");
        foreach (var (field, value) in new[]
        {
            ("endpoint", "http://customer-storage.test"), ("endpoint", "https://127.0.0.1"), ("endpoint", "https://localhost"),
            ("endpoint", "https://customer-storage.test/path"), ("endpoint", "https://user:password@customer-storage.test"),
            ("region", ""), ("bucket", "INVALID"), ("accessKeyId", ""), ("secretKey", ""), ("mode", "platform"),
        })
        {
            var invalid = new Dictionary<string, string>(valid, StringComparer.Ordinal) { [field] = value };
            using var response = await api.Client.PutAsJsonAsync(route, invalid, cancellationToken);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        await Assert.That(s3.CustomerObjects.Count).IsEqualTo(0);
        await SetAsync(api, database, "both");
        using var outsider = api.As(Ulid.NewUlid().ToString());
        using var outsiderGet = await outsider.GetAsync(route, cancellationToken);
        using var outsiderPut = await outsider.PutAsJsonAsync(route, valid, cancellationToken);
        using var outsiderDelete = await outsider.DeleteAsync(route, cancellationToken);
        using var outsiderWindow = await outsider.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        using var outsiderRestore = await outsider.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), destination = "customer" }, cancellationToken);
        foreach (var response in new[] { outsiderGet, outsiderPut, outsiderDelete, outsiderWindow, outsiderRestore })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        foreach (var source in new[] { "other", "PLATFORM", "Customer", " platform " })
        {
            using var invalidWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=" + System.Uri.EscapeDataString(source)), cancellationToken);
            using var invalidRestore = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), destination = source }, cancellationToken);
            await Assert.That(invalidWindow.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(invalidRestore.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        using var wrongCredentials = await api.Client.PutAsJsonAsync(route,
            new Dictionary<string, string>(valid, StringComparer.Ordinal) { ["secretKey"] = "wrong-secret" }, cancellationToken);
        await Assert.That(wrongCredentials.IsSuccessStatusCode).IsFalse();
        await Assert.That(s3.BadSignatures).IsGreaterThan(0);
        using var unchanged = await api.Client.GetAsync(route, cancellationToken);
        unchanged.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await unchanged.Content.ReadAsStringAsync(cancellationToken), "both");
    }

    private sealed class StalledReadStream : MemoryStream
    {
        private readonly TaskCompletionSource<int> _body = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanSeek => false;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                return await _body.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public void Release() => _body.TrySetResult(0);
    }

    private static void ManualShipping(FakeS3 s3, IServiceCollection services)
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static Dictionary<string, string> Destination(string mode) => new(StringComparer.Ordinal)
    {
        ["endpoint"] = FakeS3.CustomerEndpoint,
        ["region"] = "test-region-1",
        ["bucket"] = FakeS3.CustomerBucket,
        ["accessKeyId"] = FakeS3.CustomerAccessKey,
        ["secretKey"] = FakeS3.CustomerSecretKey,
        ["mode"] = mode,
    };

    private static HttpClient ProjectClient(ManagementApi api, string project, string scope, string? database = null) =>
        api.WithToken(api.Issuer.Mint(Ulid.NewUlid().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = scope;
            if (database is not null)
            {
                token.Claims["database"] = database;
            }
        }));

    private static async Task<DateTimeOffset> LatestAsync(HttpClient client, ManagedDatabaseRef database, string destination = "platform")
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await client.GetAsync(Uri(database.Path + "/point-in-time?destination=" + destination), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
    }

    private static async Task SetAsync(ManagementApi api, ManagedDatabaseRef database, string mode)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination(mode), cancellationToken);
        response.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await response.Content.ReadAsStringAsync(cancellationToken), mode);
    }

    private static async Task AssertDestinationAsync(string json, string mode)
    {
        using var body = JsonDocument.Parse(json);
        await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name)).IsEquivalentTo(["endpoint", "region", "bucket", "mode", "updatedAt"]);
        await Assert.That(body.RootElement.GetProperty("endpoint").GetString()!.TrimEnd('/')).IsEqualTo(FakeS3.CustomerEndpoint);
        await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("test-region-1");
        await Assert.That(body.RootElement.GetProperty("bucket").GetString()).IsEqualTo(FakeS3.CustomerBucket);
        await Assert.That(body.RootElement.GetProperty("mode").GetString()).IsEqualTo(mode);
        await Assert.That(body.RootElement.GetProperty("updatedAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.MinValue);
        await AssertPublicAsync(json);
    }

    private static async Task AssertPublicAsync(string json)
    {
        await Assert.That(json.Contains(FakeS3.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains(FakeS3.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("wrappedKey", StringComparison.Ordinal)).IsFalse();
    }

    private static async Task ShipAsync(ManagementApi api, FakeTimeProvider time, CancellationToken? cancellationToken = null)
    {
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>()
            .RunOnceAsync(cancellationToken ?? TUnit.Core.TestContext.Current!.Execution.CancellationToken);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ManagedDatabaseRef> RestoreAsync(ManagementApi api, ManagedDatabaseRef database, DateTimeOffset at, string name, string? destination = null)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var body = new Dictionary<string, object>(StringComparer.Ordinal) { ["pointInTime"] = at, ["name"] = name };
        if (destination is not null)
        {
            body["destination"] = destination;
        }

        using var response = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), body, cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var restored = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(restored.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        return new(database.Organization, database.Project, restored.GetProperty("id").GetString()!, restored.GetProperty("name").GetString()!);
    }

    private static async Task AssertDocumentAsync(HttpClient data, ManagedDatabaseRef database, string name, string text)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await data.GetAsync(Uri(ManagementApi.DocumentPath(database, "people", name)), cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(body.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    private static HashSet<string> Generations(IReadOnlyDictionary<string, byte[]> objects, ManagedDatabaseRef database) =>
        objects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))
            .Select(key => key.Split('/')[^1].Split('-')[1]).ToHashSet(StringComparer.Ordinal);

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
