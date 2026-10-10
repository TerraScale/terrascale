using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Fast.Management;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupTests
{
    [Test]
    public async Task Both_destinations_ship_encrypted_snapshots_and_wal_and_restore_actual_documents()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
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
        var customerKeys = oci.CustomerObjects.Keys.ToArray();
        await Assert.That(customerKeys.Any(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.Any(key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.All(oci.Objects.ContainsKey)).IsTrue();
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith("v1/_system/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(customerKeys.Any(key => key.Contains("_system", StringComparison.Ordinal))).IsFalse();
        await Assert.That(oci.CustomerObjects.Values.All(content => content.AsSpan().StartsWith("TBK1"u8) || content.AsSpan().StartsWith("wrapped:"u8))).IsTrue();
        await Assert.That(oci.CustomerObjects.Values.Any(content => content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(after)) >= 0)).IsFalse();
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments("customer")]
    [Arguments("both")]
    public async Task Project_tokens_select_platform_or_customer_plans_and_restore_only_within_each_stored_window(string mode)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var writer = ProjectClient(api, database.Project, "databases:read databases:write documents:write");
        using var reader = ProjectClient(api, database.Project, "databases:read", database.Name);
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
                await ErrorAssert.ErrorAsync(reader.PostAsJsonAsync(planRoute, new { pointInTime = at, destination }, cancellationToken),
                    HttpStatusCode.BadRequest, "invalid_request", "pointInTime must lie between the restore window's earliest and latest times");
                await ErrorAssert.ErrorAsync(writer.PostAsJsonAsync(restoreRoute, new { pointInTime = at, destination, name = "after-" + destination }, cancellationToken),
                    HttpStatusCode.BadRequest, "invalid_request", "pointInTime must lie between the restore window's earliest and latest times");
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

        await ErrorAssert.ErrorAsync(reader.PostAsJsonAsync(planRoute, new { pointInTime = platformSnapshot, destination = "customer" }, cancellationToken),
            HttpStatusCode.BadRequest, "invalid_request", "pointInTime must lie between the restore window's earliest and latest times");
        using var historical = await reader.PostAsJsonAsync(planRoute, new { pointInTime = customerSnapshot, destination = "customer" }, cancellationToken);
        historical.EnsureSuccessStatusCode();
        var earlier = await historical.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        await Assert.That(earlier.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(customerSnapshot);
        await Assert.That(earlier.GetProperty("segmentCount").GetInt32()).IsEqualTo(0);
        await Assert.That(earlier.GetProperty("latestSegmentAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await ErrorAssert.ErrorAsync(reader.PostAsJsonAsync(planRoute, new { pointInTime = customerLatest, destination = "Customer" }, cancellationToken),
            HttpStatusCode.BadRequest, "invalid_request", "destination must be platform or customer");
        using var narrowed = ProjectClient(api, database.Project, "databases:read databases:write", "another");
        using var hiddenWindow = await narrowed.GetAsync(Uri(database.Data + "/point-in-time?destination=customer"), cancellationToken);
        using var hiddenPlan = await narrowed.PostAsJsonAsync(planRoute, new { }, cancellationToken);
        using var hiddenRestore = await narrowed.PostAsJsonAsync(restoreRoute, new { }, cancellationToken);
        foreach (var response in new[] { hiddenWindow, hiddenPlan, hiddenRestore })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Unusable_customer_snapshot_and_segment_entries_consume_the_paginated_plan_budget(bool segments)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        await SetAsync(api, database, "customer");
        await ShipAsync(api, time);
        var latest = await LatestAsync(api.Client, database, "customer");
        var generation = Generations(oci.CustomerObjects, database).Single();
        var prefix = File(database) + (segments ? "/wal/" + generation + "/" : "/snapshots/");
        oci.ObservedListPrefix = prefix;
        // Sort beyond the segment start-after cursor; each dot-component key is filtered before parsing.
        var junk = Enumerable.Range(0, 10_000).Select(index => prefix + "zzz/./junk-" + index.ToString("D5", CultureInfo.InvariantCulture)).ToArray();
        foreach (var key in junk)
        {
            oci.SeedObject(customer: true, key, []);
        }

        using var reader = ProjectClient(api, database.Project, "databases:read");
        await ErrorAssert.ErrorAsync(reader.PostAsJsonAsync(Uri(database.Data + "/point-in-time/plan"), new { pointInTime = latest, destination = "customer" }, cancellationToken),
            HttpStatusCode.ServiceUnavailable, "pitr_plan_limit", "the point-in-time plan metadata scan exceeds 10000 objects");
        await Assert.That(oci.ListRequests).IsGreaterThanOrEqualTo(10);
        oci.CustomerListingOverride = (requested, keys) => requested == prefix
            ? [.. keys.Where(key => !key.Contains("/./", StringComparison.Ordinal))] : keys;

        using var recovered = await reader.PostAsJsonAsync(Uri(database.Data + "/point-in-time/plan"), new { pointInTime = latest, destination = "customer" }, cancellationToken);
        recovered.EnsureSuccessStatusCode();
        await Assert.That((await recovered.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(latest);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Project_destination_configuration_requires_scope_and_hides_other_projects_tenants_and_database_narrowed_tokens()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
        var database = await api.CreateManagedDatabaseAsync();
        var siblingProject = await api.CreateProjectAsync(database.Organization, "Destination sibling");
        var sibling = await api.CreateManagedDatabaseAsync(database.Organization, siblingProject, "main");
        var otherTenant = await api.CreateManagedDatabaseAsync();
        foreach (var target in new[] { database, sibling, otherTenant })
        {
            await api.VerifyProjectBindingAsync(target.Organization, target.Project);
        }

        var route = Uri($"/v1/projects/{database.Project}/backup-destination");
        using var writer = ProjectClient(api, database.Project, "databases:write");
        using var reader = ProjectClient(api, database.Project, "databases:read");
        using var saved = await writer.PutAsJsonAsync(route, Destination("customer"), cancellationToken);
        saved.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await saved.Content.ReadAsStringAsync(cancellationToken), "customer");
        var secret = oci.Secrets.Keys.Single();
        await SetAsync(api, sibling, "both");
        await SetAsync(api, otherTenant, "both");
        var secrets = oci.Secrets.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
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
            (database.Project, "databases:read databases:write", database.Name, HttpStatusCode.NotFound),
            (database.Project, "databases:read databases:write", "missing", HttpStatusCode.NotFound),
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
        await Assert.That(oci.Secrets).IsEquivalentTo(secrets);
        await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
        using var removed = await writer.DeleteAsync(route, cancellationToken);
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var absent = await reader.GetAsync(route, cancellationToken);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([secret]);
        foreach (var target in new[] { sibling, otherTenant })
        {
            using var client = ProjectClient(api, target.Project, "databases:read");
            await AssertDestinationAsync(await client.GetStringAsync(Uri($"/v1/projects/{target.Project}/backup-destination"), cancellationToken), "both");
        }

        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Customer_only_shipping_is_isolated_to_its_project_and_keeps_system_backups_on_platform()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
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

        await Assert.That(oci.CustomerObjects.Keys.Any(key => key.StartsWith(File(customer) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith(File(sibling) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith(File(otherOrganization) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith("v1/_system/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(oci.CustomerObjects.Keys.All(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal)
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
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Credentials_live_in_vault_only_and_delete_resumes_platform_without_removing_customer_objects()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        using var unset = await api.Client.GetAsync(route, cancellationToken);
        await Assert.That(unset.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await SetAsync(api, database, "customer");
        using var get = await api.Client.GetAsync(route, cancellationToken);
        get.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await get.Content.ReadAsStringAsync(cancellationToken), "customer");
        await Assert.That(oci.Secrets.Count).IsEqualTo(1);
        var secretId = oci.Secrets.Keys.Single();
        using var secret = JsonDocument.Parse(oci.Secrets.Values.Single());
        await Assert.That(secret.RootElement.GetProperty("version").GetInt32()).IsEqualTo(1);
        await Assert.That(secret.RootElement.GetProperty("accessKeyId").GetString()).IsEqualTo(FakeOci.CustomerAccessKey);
        await Assert.That(secret.RootElement.GetProperty("secretKey").GetString()).IsEqualTo(FakeOci.CustomerSecretKey);
        var catalog = string.Join('\n', await DocumentFiles.ReadAsync(Path.Combine(api.Server.DataDirectory, "_system.db")));
        await Assert.That(catalog.Contains(FakeOci.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(catalog.Contains(FakeOci.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
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
        var retained = oci.CustomerObjects.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal);
        using var deleted = await api.Client.DeleteAsync(route, cancellationToken);
        using var gone = await api.Client.GetAsync(route, cancellationToken);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([secretId]);
        await Assert.That(oci.SecretDeletionTimes[secretId]).IsGreaterThanOrEqualTo(time.GetUtcNow().AddDays(7));
        await ShipAsync(api, time);
        await Assert.That(oci.CustomerObjects.Keys).IsEquivalentTo(retained.Keys);
        await Assert.That(retained.All(item => oci.CustomerObjects[item.Key].AsSpan().SequenceEqual(item.Value))).IsTrue();
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        using var customerWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        await Assert.That(customerWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database), "platform-resumed");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_empty_project_with_a_customer_backup_destination_requires_detach_before_deletion(bool viaCommit)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        var queryClock = new SteppingClock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services =>
        {
            oci.Route(services);
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, queryClock);
        }, time);
        var organization = await api.CreateOrganizationAsync(new Faker().Company.CompanyName());
        var project = await api.CreateProjectAsync(organization, "Empty project");
        var projectRoute = Uri($"/v1/management/organizations/{organization}/projects/{project}");
        var destinationRoute = Uri(projectRoute.OriginalString + "/backup-destination");
        using var saved = await api.Client.PutAsJsonAsync(destinationRoute, Destination("customer"), cancellationToken);
        saved.EnsureSuccessStatusCode();
        var secretId = oci.Secrets.Keys.Single();
        var secret = oci.Secrets[secretId];

        using var rejected = await DeleteProjectAsync();
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using var configured = await api.Client.GetAsync(destinationRoute, cancellationToken);
        configured.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await configured.Content.ReadAsStringAsync(cancellationToken), "customer");
        await Assert.That(oci.Secrets.Keys).IsEquivalentTo([secretId]);
        await Assert.That(oci.Secrets[secretId]).IsEqualTo(secret);
        await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);

        using var detached = await api.Client.DeleteAsync(destinationRoute, cancellationToken);
        await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var unconfigured = await api.Client.GetAsync(destinationRoute, cancellationToken);
        await Assert.That(unconfigured.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([secretId]);
        await Assert.That(oci.SecretDeletionTimes[secretId]).IsGreaterThanOrEqualTo(time.GetUtcNow().AddDays(7));
        // System catalog scans retain the engine safety budget, even inside a Hobby management commit.
        queryClock.Armed = true;
        using var deleted = await DeleteProjectAsync();
        await Assert.That(deleted.StatusCode).IsEqualTo(viaCommit ? HttpStatusCode.OK : HttpStatusCode.NoContent)
            .Because(await deleted.Content.ReadAsStringAsync(cancellationToken));
        using var gone = await api.Client.GetAsync(projectRoute, cancellationToken);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([secretId]);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);

        Task<HttpResponseMessage> DeleteProjectAsync()
        {
            return viaCommit
                ? api.Client.PostAsJsonAsync(Uri("/v1/management/commits"), new
                {
                    id = Ulid.NewUlid().ToString(),
                    ops = new[] { new { op = "delete_project", organization, project } },
                }, cancellationToken)
                : api.Client.DeleteAsync(projectRoute, cancellationToken);
        }
    }

    [Test]
    public async Task Creating_replacing_and_detaching_a_backup_destination_advances_the_organization_fence()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
        var organization = await api.CreateOrganizationAsync(new Faker().Company.CompanyName());
        var project = await api.CreateProjectAsync(organization, "Backup fencing");
        var route = Uri($"/v1/management/organizations/{organization}/projects/{project}/backup-destination");

        foreach (var mode in new[] { "customer", "both" })
        {
            var before = await VersionAsync();
            using var saved = await api.Client.PutAsJsonAsync(route, Destination(mode), cancellationToken);
            saved.EnsureSuccessStatusCode();
            await AssertDestinationAsync(await saved.Content.ReadAsStringAsync(cancellationToken), mode);
            await AssertChangedAndStaleFenceAsync(before);
        }

        var beforeDetach = await VersionAsync();
        using var detached = await api.Client.DeleteAsync(route, cancellationToken);
        await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await AssertChangedAndStaleFenceAsync(beforeDetach);

        var beforeRepeatedDelete = await VersionAsync();
        using var repeated = await api.Client.DeleteAsync(route, cancellationToken);
        await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await VersionAsync()).IsEqualTo(beforeRepeatedDelete);

        async Task<string> VersionAsync()
        {
            using var response = await api.Client.GetAsync(Uri($"/v1/management/versions?organization={organization}"), cancellationToken);
            response.EnsureSuccessStatusCode();
            var versions = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            return versions.GetProperty("organizations").GetProperty(organization).GetString()!;
        }

        async Task AssertChangedAndStaleFenceAsync(string before)
        {
            var after = await VersionAsync();
            await Assert.That(after).IsNotEqualTo(before);
            using var rejected = await api.Client.PostAsJsonAsync(Uri("/v1/management/commits"), new
            {
                id = Ulid.NewUlid().ToString(),
                ops = new[] { new { op = "fence", organization, version = before } },
            }, cancellationToken);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
            var body = await rejected.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("precondition_failed");
        }
    }

    [Test]
    public async Task Changing_a_destination_starts_a_new_generation_and_stops_platform_shipping_in_customer_mode()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
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
        var initial = Generations(oci.CustomerObjects, database);
        await Assert.That(initial.Count).IsEqualTo(1);
        var oldSecret = oci.Secrets.Keys.Single();
        var platformKeys = oci.Objects.Keys.Where(key => key.StartsWith(File(database) + "/", StringComparison.Ordinal)).ToArray();
        await SetAsync(api, database, "customer");
        await PutAsync(data, database, "after", after);
        await ShipAsync(api, time);
        var changed = Generations(oci.CustomerObjects, database);
        await Assert.That(changed.Count).IsEqualTo(2);
        await Assert.That(changed.IsSupersetOf(initial)).IsTrue();
        await Assert.That(oci.Objects.Keys.Where(key => key.StartsWith(File(database) + "/", StringComparison.Ordinal))).IsEquivalentTo(platformKeys);
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
        await Assert.That(oci.Secrets.Count).IsEqualTo(2);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([oldSecret]);
        await Assert.That(oci.SecretDeletionTimes[oldSecret]).IsGreaterThanOrEqualTo(time.GetUtcNow().AddDays(7));
        await Assert.That(oci.ScheduledSecrets.Contains(oci.Secrets.Keys.Single(id => id != oldSecret), StringComparer.Ordinal)).IsFalse();
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Invalid_destinations_and_unauthorized_callers_cannot_change_or_restore_customer_backups()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
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

        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
        await Assert.That(oci.CustomerObjects.Count).IsEqualTo(0);
        await SetAsync(api, database, "both");
        var viewerId = await api.AddMemberAsync(api.Client, database.Organization, "viewer");
        using var viewer = api.As(viewerId);
        using var outsider = api.As(Ulid.NewUlid().ToString());
        using var viewerPut = await viewer.PutAsJsonAsync(route, valid, cancellationToken);
        using var viewerDelete = await viewer.DeleteAsync(route, cancellationToken);
        using var viewerRestore = await viewer.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), destination = "customer" }, cancellationToken);
        await Assert.That(viewerPut.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(viewerDelete.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(viewerRestore.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
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
        await Assert.That(oci.BadSignatures).IsGreaterThan(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(1);
        using var unchanged = await api.Client.GetAsync(route, cancellationToken);
        unchanged.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await unchanged.Content.ReadAsStringAsync(cancellationToken), "both");
    }

    [Test]
    public async Task A_partial_mirror_upload_recovers_without_overlapping_segments_and_both_copies_restore()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "both");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var faker = new Faker();
        var texts = Enumerable.Range(0, 4).Select(_ => faker.Lorem.Sentence(12)).ToArray();
        await PutAsync(data, database, "initial", texts[0]);
        await ShipAsync(api, time);
        var initial = Generations(oci.CustomerObjects, database);

        // The platform accepts the segment; the customer refuses its copy.
        oci.RejectCustomerWrites = key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal);
        await PutAsync(data, database, "partial", texts[1]);
        await ShipAsync(api, time);
        var partialAt = time.GetUtcNow();
        var partialSegments = oci.Objects.Keys.Where(key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal)).ToArray();
        await Assert.That(partialSegments.Length).IsEqualTo(1);
        await Assert.That(partialSegments.Any(oci.CustomerObjects.ContainsKey)).IsFalse();

        // A write arrives before recovery. Its fresh generation must snapshot both writes.
        await PutAsync(data, database, "recovered", texts[2]);
        oci.RejectCustomerWrites = _ => false;
        await ShipAsync(api, time);
        await Assert.That(Generations(oci.CustomerObjects, database).Count).IsEqualTo(initial.Count + 1);
        await PutAsync(data, database, "later", texts[3]);
        await ShipAsync(api, time);

        var names = new[] { "initial", "partial", "recovered", "later" };
        foreach (var destination in new[] { "platform", "customer" })
        {
            var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database, destination), "recovered-" + destination, destination);
            for (var index = 0; index < texts.Length; index++)
            {
                await AssertDocumentAsync(data, restored, names[index], texts[index]);
            }
        }

        var earlier = await RestoreAsync(api, database, partialAt, "partial-platform", "platform");
        await AssertDocumentAsync(data, earlier, "initial", texts[0]);
        await AssertDocumentAsync(data, earlier, "partial", texts[1]);
        using var absent = await data.GetAsync(Uri(ManagementApi.DocumentPath(earlier, "people", "recovered")), cancellationToken);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var objects in new[] { oci.Objects, oci.CustomerObjects })
        {
            var segments = objects.Keys.Where(key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal)).ToArray();
            var starts = segments.Select(key => key[..key.LastIndexOf('-')]).ToArray();
            await Assert.That(starts.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(segments.Length);
        }

        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Hostile_customer_listings_cannot_read_or_delete_foreign_project_objects()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        var foreignProject = await api.CreateProjectAsync(database.Organization, "Foreign project");
        var foreign = await api.CreateManagedDatabaseAsync(database.Organization, foreignProject, "main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var foreignData = await api.CreateClientAsync(foreign.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "both");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        await ManagementApi.CreateCollectionAsync(foreignData, foreign, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await PutAsync(foreignData, foreign, "before", text);
        await ShipAsync(api, time);
        var foreignSnapshot = oci.Objects.Keys.Single(key => key.StartsWith(File(foreign) + "/snapshots/", StringComparison.Ordinal));
        await PutAsync(foreignData, foreign, "after", new Faker().Lorem.Sentence(12));
        await ShipAsync(api, time);
        var foreignSegment = oci.Objects.Keys.Single(key => key.StartsWith(File(foreign) + "/wal/", StringComparison.Ordinal));
        var preserved = new[] { foreignSnapshot, foreignSegment }.ToDictionary(key => key, key => oci.Objects[key].ToArray(), StringComparer.Ordinal);
        // Foreign objects in the customer bucket are not this project's retention responsibility.
        foreach (var item in preserved)
        {
            oci.SeedObject(customer: true, item.Key, item.Value);
        }
        for (var day = 0; day < 2; day++)
        {
            await PutAsync(data, database, "day-" + day, new Faker().Lorem.Sentence(12));
            time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            await ShipAsync(api, time);
        }

        var latest = await LatestAsync(api.Client, database, "customer");
        var foreignReads = 0;
        var foreignDeletes = 0;
        var snapshotListings = 0;
        var segmentListings = 0;
        oci.ObjectRequested = (customer, method, key) =>
        {
            if (customer && preserved.ContainsKey(key))
            {
                if (method == HttpMethod.Get)
                {
                    Interlocked.Increment(ref foreignReads);
                }

                if (method == HttpMethod.Delete)
                {
                    Interlocked.Increment(ref foreignDeletes);
                }
            }
        };
        oci.CustomerListingOverride = (prefix, keys) =>
        {
            if (prefix == File(database) + "/snapshots/")
            {
                Interlocked.Increment(ref snapshotListings);
                return [foreignSnapshot, .. keys];
            }

            if (prefix.StartsWith(File(database) + "/wal/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref segmentListings);
                return [foreignSegment, .. keys];
            }

            return keys;
        };
        time.Advance(TimeSpan.FromHours(2));
        await ShipAsync(api, time);

        // With valid snapshots, retention and restore must also validate the WAL listing independently.
        oci.CustomerListingOverride = (prefix, keys) =>
        {
            if (prefix.StartsWith(File(database) + "/wal/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref segmentListings);
                return [foreignSegment, .. keys];
            }

            return keys;
        };
        time.Advance(TimeSpan.FromHours(1));
        await ShipAsync(api, time);
        using var rejectedSegment = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, name = "hostile-segment", destination = "customer" }, cancellationToken);
        if (rejectedSegment.IsSuccessStatusCode)
        {
            var body = await rejectedSegment.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var restoredSegment = new ManagedDatabaseRef(database.Organization, database.Project, body.GetProperty("id").GetString()!, body.GetProperty("name").GetString()!);
            await AssertDocumentAsync(data, restoredSegment, "kept", text);
        }
        else
        {
            await AssertPublicAsync(await rejectedSegment.Content.ReadAsStringAsync(cancellationToken));
        }

        // A restore also receives a foreign snapshot alone: rejecting it and ignoring it are both safe.
        oci.CustomerListingOverride = (prefix, keys) => prefix == File(database) + "/snapshots/" ? [foreignSnapshot] : keys;
        using var rejected = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, name = "hostile-listing", destination = "customer" }, cancellationToken);
        await Assert.That(rejected.IsSuccessStatusCode).IsFalse();
        await AssertPublicAsync(await rejected.Content.ReadAsStringAsync(cancellationToken));
        await Assert.That(snapshotListings).IsGreaterThan(0);
        await Assert.That(segmentListings).IsGreaterThan(0);
        await Assert.That(foreignReads).IsEqualTo(0);
        await Assert.That(foreignDeletes).IsEqualTo(0);
        await Assert.That(preserved.All(item => oci.CustomerObjects.TryGetValue(item.Key, out var bytes) && bytes.AsSpan().SequenceEqual(item.Value))).IsTrue();
        oci.CustomerListingOverride = null;
        oci.ObjectRequested = null;
        var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database, "customer"), "safe-listing", "customer");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_truncated_customer_pages_cannot_stall_backup_passes_or_another_projects_shipping()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var customer = await api.CreateManagedDatabaseAsync();
        var siblingProject = await api.CreateProjectAsync(customer.Organization, "Platform sibling");
        var sibling = await api.CreateManagedDatabaseAsync(customer.Organization, siblingProject, "main");
        using var data = await api.CreateClientAsync(customer.Organization, "documents:read", "documents:write");
        await SetAsync(api, customer, "both");
        await ManagementApi.CreateCollectionAsync(data, customer, "people");
        await ManagementApi.CreateCollectionAsync(data, sibling, "people");
        var faker = new Faker();
        var texts = Enumerable.Range(0, 3).Select(_ => faker.Lorem.Sentence(12)).ToArray();
        string[] names = ["first", "second"];
        await PutAsync(data, customer, "initial", texts[0]);
        await PutAsync(data, sibling, "initial", texts[0]);
        using var stop = new CancellationTokenSource();
        var pass = Task.CompletedTask;
        async Task RunPassAsync()
        {
            stop.CancelAfter(TimeSpan.FromSeconds(10));
            pass = ShipAsync(api, time, stop.Token);
            await pass.WaitAsync(TimeSpan.FromSeconds(10));
            stop.CancelAfter(Timeout.InfiniteTimeSpan);
        }

        try
        {
            await RunPassAsync();
            var prefix = File(customer) + "/";
            var listings = 0;
            oci.EmptyCustomerPages = requested =>
            {
                if (!requested.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }

                Interlocked.Increment(ref listings);
                return true;
            };
            for (var index = 0; index < names.Length; index++)
            {
                await PutAsync(data, customer, names[index], texts[index + 1]);
                await PutAsync(data, sibling, names[index], texts[index + 1]);
                // Retention must encounter the hostile listing on each pass, even after the previous failure.
                time.Advance(TimeSpan.FromHours(1));
                var before = Volatile.Read(ref listings);
                await RunPassAsync();
                await Assert.That(Volatile.Read(ref listings)).IsGreaterThan(before);
            }

            var platformRestore = await RestoreAsync(api, sibling, await LatestAsync(api.Client, sibling), "unstalled-platform", "platform");
            await AssertDocumentAsync(data, platformRestore, "initial", texts[0]);
            for (var index = 0; index < names.Length; index++)
            {
                await AssertDocumentAsync(data, platformRestore, names[index], texts[index + 1]);
            }

            oci.EmptyCustomerPages = null;
            var customerRestore = await RestoreAsync(api, customer, await LatestAsync(api.Client, customer, "customer"), "unstalled-customer", "customer");
            await AssertDocumentAsync(data, customerRestore, "initial", texts[0]);
            for (var index = 0; index < names.Length; index++)
            {
                await AssertDocumentAsync(data, customerRestore, names[index], texts[index + 1]);
            }

            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            oci.EmptyCustomerPages = null;
            await stop.CancelAsync();
            await pass.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Stalled_customer_snapshot_body_is_cancelled_at_deadline_and_releases_shipping_for_the_next_pass()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var customer = await api.CreateManagedDatabaseAsync();
        var siblingProject = await api.CreateProjectAsync(customer.Organization, "Platform sibling");
        var sibling = await api.CreateManagedDatabaseAsync(customer.Organization, siblingProject, "main");
        using var data = await api.CreateClientAsync(customer.Organization, "documents:read", "documents:write");
        await SetAsync(api, customer, "customer");
        await ManagementApi.CreateCollectionAsync(data, customer, "people");
        await ManagementApi.CreateCollectionAsync(data, sibling, "people");
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var after = faker.Lorem.Sentence(12);
        await PutAsync(data, customer, "before", before);
        await PutAsync(data, sibling, "before", before);
        using var stalled = new StalledReadStream();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var prefix = File(customer) + "/snapshots/";
        oci.CustomerContentOverride = key =>
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            var content = new StreamContent(stalled);
            content.Headers.ContentLength = oci.CustomerObjects[key].LongLength;
            return content;
        };
        // The very first pass drills its newly uploaded snapshot: headers arrive, but its body never does.
        var pass = ShipAsync(api, time, stop.Token);
        try
        {
            await stalled.Entered.Task.WaitAsync(stop.Token);
            await Assert.That(stalled.Cancelled.Task.IsCompleted).IsFalse();
            await Assert.That(pass.IsCompleted).IsFalse();
            time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
            await stalled.Cancelled.Task.WaitAsync(stop.Token);
            await pass.WaitAsync(stop.Token);
            await Assert.That(stop.IsCancellationRequested).IsFalse();
            var drill = backup.Status.Files.Single(file => file.File == File(customer)).LastDrill!;
            await Assert.That(drill.Passed).IsFalse();

            oci.CustomerContentOverride = null;
            await PutAsync(data, customer, "after", after);
            await PutAsync(data, sibling, "after", after);
            pass = ShipAsync(api, time, stop.Token);
            await pass.WaitAsync(stop.Token);
            foreach (var (database, destination) in new[] { (customer, "customer"), (sibling, "platform") })
            {
                var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database, destination), "deadline-" + destination, destination);
                await AssertDocumentAsync(data, restored, "before", before);
                await AssertDocumentAsync(data, restored, "after", after);
            }

            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            oci.CustomerContentOverride = null;
            await stop.CancelAsync();
            stalled.Release();
            await pass.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Cancelled_mirror_wal_upload_abandons_the_generation_before_the_next_pass()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "both");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var faker = new Faker();
        var texts = Enumerable.Range(0, 4).Select(_ => faker.Lorem.Sentence(12)).ToArray();
        await PutAsync(data, database, "initial", texts[0]);
        await ShipAsync(api, time);
        var initial = Generations(oci.CustomerObjects, database);
        await Assert.That(initial.Count).IsEqualTo(1);
        using var stop = new CancellationTokenSource();
        var reached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oci.BeforeCustomerWrite = async (key, cancellationToken) =>
        {
            if (key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal))
            {
                reached.TrySetResult(key);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        };
        await PutAsync(data, database, "partial", texts[1]);
        var pass = backup.RunOnceAsync(stop.Token);
        string partialSegment;
        try
        {
            partialSegment = await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(oci.Objects.ContainsKey(partialSegment)).IsTrue();
            await Assert.That(oci.CustomerObjects.ContainsKey(partialSegment)).IsFalse();
            await stop.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pass.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await stop.CancelAsync();
            release.TrySetResult();
            oci.BeforeCustomerWrite = null;
            await pass.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await Assert.That(backup.Status.Files.Single(file => file.File == File(database)).Generation).IsNull();
        await PutAsync(data, database, "recovered", texts[2]);
        await ShipAsync(api, time);
        await Assert.That(Generations(oci.CustomerObjects, database).Count).IsEqualTo(initial.Count + 1);
        await Assert.That(oci.CustomerObjects.ContainsKey(partialSegment)).IsFalse();
        await PutAsync(data, database, "later", texts[3]);
        await ShipAsync(api, time);
        var names = new[] { "initial", "partial", "recovered", "later" };
        foreach (var destination in new[] { "platform", "customer" })
        {
            var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database, destination), "cancelled-" + destination, destination);
            for (var index = 0; index < texts.Length; index++)
            {
                await AssertDocumentAsync(data, restored, names[index], texts[index]);
            }
        }

        foreach (var objects in new[] { oci.Objects, oci.CustomerObjects })
        {
            var segments = objects.Keys.Where(key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal)).ToArray();
            await Assert.That(segments.Select(key => key[..key.LastIndexOf('-')]).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(segments.Length);
        }

        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Customer_restore_accepts_exactly_sixteen_kibibytes_of_wrapped_key_and_rejects_larger_bodies()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "customer");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        var key = oci.CustomerObjects.Single(item => item.Key.StartsWith($"v1/keys/{database.Organization}/{database.Project}/", StringComparison.Ordinal));
        var latest = await LatestAsync(api.Client, database, "customer");
        // Trailing whitespace decodes to the same key. Exactly 16 KiB must remain accepted, catching >= regressions.
        var keyBody = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(key.Value).PadRight(16 * 1024));
        var keyReads = 0;
        oci.CustomerReadOverride = requested =>
        {
            if (requested != key.Key)
            {
                return null;
            }

            Interlocked.Increment(ref keyReads);
            return keyBody;
        };
        var boundary = await RestoreAsync(api, database, latest, "boundary-key", "customer");
        await AssertDocumentAsync(data, boundary, "kept", text);
        await Assert.That(keyReads).IsGreaterThan(0);
        keyReads = 0;
        keyBody = [.. keyBody, (byte)' '];
        using var rejected = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, name = "oversized-key", destination = "customer" }, cancellationToken);
        await Assert.That(keyReads).IsGreaterThan(0);
        await Assert.That(rejected.IsSuccessStatusCode).IsFalse();
        var error = await rejected.Content.ReadAsStringAsync(cancellationToken);
        await AssertPublicAsync(error);
        await Assert.That(error.Contains(Encoding.ASCII.GetString(key.Value), StringComparison.Ordinal)).IsFalse();
        await AssertDocumentAsync(data, database, "kept", text);
        oci.CustomerReadOverride = null;
        var restored = await RestoreAsync(api, database, latest, "bounded-key", "customer");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Retention_trims_platform_snapshots_from_before_customer_attachment_and_retries_platform_locks_independently()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        var oldPlatform = oci.Objects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        await Assert.That(oldPlatform.Count).IsEqualTo(1);
        await SetAsync(api, database, "both");
        await ShipAsync(api, time);
        var oldCustomer = oci.CustomerObjects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal)).ToArray();
        await Assert.That(oldCustomer.Length).IsEqualTo(1);
        oci.Locked = oldPlatform.Contains;
        for (var day = 0; day < 2; day++)
        {
            time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            await ShipAsync(api, time);
        }

        time.Advance(TimeSpan.FromHours(2));
        await ShipAsync(api, time);
        await Assert.That(oldPlatform.All(oci.Objects.ContainsKey)).IsTrue();
        await Assert.That(oldCustomer.Any(oci.CustomerObjects.ContainsKey)).IsFalse();
        oci.Locked = _ => false;
        time.Advance(TimeSpan.FromHours(1));
        await ShipAsync(api, time);
        await Assert.That(oldPlatform.Any(oci.Objects.ContainsKey)).IsFalse();
        foreach (var destination in new[] { "platform", "customer" })
        {
            var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database, destination), "retained-" + destination, destination);
            await AssertDocumentAsync(data, restored, "kept", text);
        }

        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Customer_only_switch_refreshes_size_and_prunes_old_platform_history_while_expired_window_is_absent()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        await ShipAsync(api, time);
        var old = oci.Objects.Keys.Single(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal));
        var measured = await BackupRecordAsync(api, database);
        await Assert.That(measured.GetProperty("sizeBytes").GetInt64()).IsGreaterThan(0);
        var previousMeasurement = measured.GetProperty("sizedAt").GetDateTimeOffset();

        await SetAsync(api, database, "customer");
        await ShipAsync(api, time);
        var switched = await BackupRecordAsync(api, database);
        await Assert.That(switched.GetProperty("sizeBytes").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(switched.GetProperty("sizedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await ShipAsync(api, time);
        var refreshed = await BackupRecordAsync(api, database);
        await Assert.That(refreshed.GetProperty("sizeBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(refreshed.GetProperty("sizedAt").GetDateTimeOffset()).IsGreaterThan(previousMeasurement);

        // Retention preserves the newest platform snapshot before the cutoff; seed an older one to prune.
        var parts = old.Split('/')[^1].Split('-');
        var expired = string.Create(CultureInfo.InvariantCulture,
            $"{File(database)}/snapshots/{time.GetUtcNow().AddDays(-2).ToUnixTimeMilliseconds():D13}-{parts[1]}-{parts[2]}");
        oci.SeedObject(customer: false, expired, []);
        time.Advance(TimeSpan.FromDays(2));
        await ShipAsync(api, time);
        await ShipAsync(api, time);
        await Assert.That(oci.Objects.ContainsKey(expired)).IsFalse();
        await Assert.That(oci.Objects.ContainsKey(old)).IsTrue();
        using var platformWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=platform"), cancellationToken);
        await Assert.That(platformWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var platformRestore = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = time.GetUtcNow(), name = "expired-platform", destination = "platform" }, cancellationToken);
        await Assert.That(platformRestore.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var restored = await RestoreAsync(api, database, await LatestAsync(api.Client, database, "customer"), "retained-customer", "customer");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Own_prefix_junk_is_ignored_but_nonascending_customer_listings_still_fail()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "customer");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        var snapshot = oci.CustomerObjects.Keys.Single(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal));
        var prefix = File(database) + "/snapshots/";
        string[] junk = [prefix + new string('x', 1025), prefix + "./" + snapshot.Split('/')[^1],
            prefix + "junk/../" + snapshot.Split('/')[^1], prefix + "negative", prefix + "invalid-name"];
        var touched = 0;
        oci.CustomerListingOverride = (requested, keys) => requested == prefix ? [.. keys.Concat(junk).Order(StringComparer.Ordinal)] : keys;
        oci.CustomerListingSizeOverride = key => key == prefix + "negative" ? -1 : null;
        oci.ObjectRequested = (customer, method, key) =>
        {
            if (customer && (method == HttpMethod.Get || method == HttpMethod.Delete) && junk.Contains(key, StringComparer.Ordinal))
            {
                Interlocked.Increment(ref touched);
            }
        };
        await ShipAsync(api, time);
        using var validWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        validWindow.EnsureSuccessStatusCode();
        var latest = (await validWindow.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
        var restored = await RestoreAsync(api, database, latest, "junk-ignored", "customer");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(touched).IsEqualTo(0);
        foreach (var repeated in new[] { true, false })
        {
            oci.CustomerListingOverride = (requested, keys) => requested != prefix ? keys
                : repeated ? [snapshot, snapshot] : [prefix + "z-last", snapshot];
            using var rejected = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
            await Assert.That(rejected.IsSuccessStatusCode).IsFalse();
            await AssertPublicAsync(await rejected.Content.ReadAsStringAsync(cancellationToken));
            await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = latest, destination = "customer" }, cancellationToken),
                HttpStatusCode.ServiceUnavailable, "pitr_plan_unavailable", "the point-in-time plan metadata could not be read");
        }

        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Missing_customer_bucket_is_unavailable_while_a_missing_snapshot_is_an_incomplete_backup()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "customer");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        var snapshot = oci.CustomerObjects.Keys.Single(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal));
        var latest = await LatestAsync(api.Client, database, "customer");
        foreach (var (code, status) in new[] { ("NoSuchBucket", HttpStatusCode.ServiceUnavailable), ("NoSuchKey", HttpStatusCode.InternalServerError) })
        {
            oci.CustomerReadError = key => key == snapshot ? code : null;
            using var rejected = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
                new { pointInTime = latest, name = "missing-" + code.ToLowerInvariant(), destination = "customer" }, cancellationToken);
            await Assert.That(rejected.StatusCode).IsEqualTo(status);
            if (code == "NoSuchBucket")
            {
                await AssertUnavailableAsync(rejected, "backup bucket or OCI Vault could not be accessed");
            }

            await AssertPublicAsync(await rejected.Content.ReadAsStringAsync(cancellationToken));
        }

        oci.CustomerReadError = null;
        var restored = await RestoreAsync(api, database, latest, "bucket-returned", "customer");
        await AssertDocumentAsync(data, restored, "kept", text);
    }

    [Test]
    [Arguments("non-json")]
    [Arguments("null")]
    [Arguments("missing-id")]
    [Arguments("invalid-id")]
    [Arguments("oversized")]
    public async Task Malformed_vault_create_answers_fixed_unavailable_without_replacing_the_current_destination(string fault)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        await SetAsync(api, database, "customer");
        var currentSecret = oci.Secrets.Keys.Single();
        var marker = "vault-protocol-marker-" + Guid.NewGuid().ToString("N");
        oci.SecretResponseOverride = (method, path) => method == HttpMethod.Post && path == "/20180608/secrets"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MalformedVaultBody(fault, bundle: false, marker), Encoding.UTF8, "application/json") }
            : null;
        using var failed = await api.Client.PutAsJsonAsync(route, Destination("both"), cancellationToken);
        await AssertUnavailableAsync(failed, "customer backup bucket or OCI Vault could not be accessed", marker);
        using var unchanged = await api.Client.GetAsync(route, cancellationToken);
        unchanged.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await unchanged.Content.ReadAsStringAsync(cancellationToken), "customer");
        await Assert.That(oci.ScheduledSecrets.Contains(currentSecret, StringComparer.Ordinal)).IsFalse();
        oci.SecretResponseOverride = null;
        // The failed request releases admission; its successor can publish and retire the predecessor.
        await SetAsync(api, database, "both");
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([currentSecret]);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments("non-json")]
    [Arguments("null")]
    [Arguments("oversized")]
    [Arguments("missing-content")]
    [Arguments("wrong-content-type")]
    [Arguments("invalid-base64")]
    [Arguments("wrong-version")]
    [Arguments("invalid-credentials")]
    public async Task Malformed_vault_bundle_answers_fixed_unavailable_for_window_and_restore(string fault)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "customer");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        var latest = await LatestAsync(api.Client, database, "customer");
        var marker = "vault-protocol-marker-" + Guid.NewGuid().ToString("N");
        oci.SecretResponseOverride = (method, path) => method == HttpMethod.Get && path.StartsWith("/20190301/secretbundles/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MalformedVaultBody(fault, bundle: true, marker), Encoding.UTF8, "application/json") }
            : null;
        using var window = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        using var restore = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, name = "malformed-bundle", destination = "customer" }, cancellationToken);
        await AssertUnavailableAsync(window, "backup bucket or OCI Vault could not be accessed", marker);
        await AssertUnavailableAsync(restore, "backup bucket or OCI Vault could not be accessed", marker);
        oci.SecretResponseOverride = null;
        var restored = await RestoreAsync(api, database, latest, "bundle-recovered", "customer");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Retirement_failure_preserves_committed_save_and_delete_and_releases_organization_admission(bool protocolFailure)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        var siblingProject = await api.CreateProjectAsync(database.Organization, "Retirement sibling");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "both");
        var oldSecret = oci.Secrets.Keys.Single();
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        await PutAsync(data, database, "kept", new Faker().Lorem.Sentence(12));
        await ShipAsync(api, time);
        var preserved = oci.CustomerObjects.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal);
        var marker = "retirement-marker-" + Guid.NewGuid().ToString("N");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirements = 0;
        oci.BeforeSecretRequest = async (_, path, cancellationToken) =>
        {
            if (!path.EndsWith("/actions/scheduleDeletion", StringComparison.Ordinal))
            {
                return;
            }

            Interlocked.Increment(ref retirements);
            if (path.Contains(oldSecret, StringComparison.Ordinal))
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            // Deletion ignores response bodies; inject a transport protocol failure to exercise its explicit catch.
            if (protocolFailure)
            {
                throw new InvalidDataException(marker);
            }
        };
        oci.SecretResponseOverride = (_, path) => path.EndsWith("/actions/scheduleDeletion", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(marker) } : null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var saving = api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination("customer"), stop.Token);
        try
        {
            await reached.Task.WaitAsync(stop.Token);
            using var committed = await api.Client.GetAsync(Uri(database.ProjectPath + "/backup-destination"), stop.Token);
            committed.EnsureSuccessStatusCode();
            await AssertDestinationAsync(await committed.Content.ReadAsStringAsync(cancellationToken), "customer");
            var currentSecret = oci.Secrets.Keys.Single(id => id != oldSecret);
            using var sibling = await api.Client.PutAsJsonAsync(
                Uri($"/v1/management/organizations/{database.Organization}/projects/{siblingProject}/backup-destination"), Destination("customer"), stop.Token);
            sibling.EnsureSuccessStatusCode();
            release.TrySetResult();
            using var saved = await saving.WaitAsync(stop.Token);
            saved.EnsureSuccessStatusCode();
            await AssertDestinationAsync(await saved.Content.ReadAsStringAsync(cancellationToken), "customer");
            await Assert.That(oci.ScheduledSecrets.Contains(currentSecret, StringComparer.Ordinal)).IsFalse();
            using var deleted = await api.Client.DeleteAsync(Uri(database.ProjectPath + "/backup-destination"), stop.Token);
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            using var gone = await api.Client.GetAsync(Uri(database.ProjectPath + "/backup-destination"), stop.Token);
            await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(retirements).IsEqualTo(2);
            await Assert.That(preserved.All(item => oci.CustomerObjects.TryGetValue(item.Key, out var bytes) && bytes.AsSpan().SequenceEqual(item.Value))).IsTrue();
        }
        finally
        {
            release.TrySetResult();
            await stop.CancelAsync();
            await ((Task)saving).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (saving.IsCompletedSuccessfully)
            {
                using var response = await saving;
            }
        }
    }

    [Test]
    public async Task Destination_save_admission_authorizes_first_limits_each_organization_and_bounds_global_saves()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => ManualShipping(oci, services), time);
        var databases = new List<ManagedDatabaseRef>();
        for (var index = 0; index < 5; index++)
        {
            databases.Add(await api.CreateManagedDatabaseAsync());
        }

        var sibling = await api.CreateProjectAsync(databases[0].Organization, "Admission sibling");
        var viewerId = await api.AddMemberAsync(api.Client, databases[0].Organization, "viewer");
        using var viewer = api.As(viewerId);
        using var outsider = api.As(Ulid.NewUlid().ToString());
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creates = 0;
        oci.BeforeSecretRequest = async (method, path, cancellationToken) =>
        {
            if (method != HttpMethod.Post || path != "/20180608/secrets")
            {
                return;
            }

            var count = Interlocked.Increment(ref creates);
            firstEntered.TrySetResult();
            if (count == 4)
            {
                fourEntered.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = new List<Task<HttpResponseMessage>>();
        try
        {
            var firstRoute = Uri(databases[0].ProjectPath + "/backup-destination");
            pending.Add(api.Client.PutAsJsonAsync(firstRoute, Destination("customer"), stop.Token));
            await firstEntered.Task.WaitAsync(stop.Token);
            using var duplicate = await api.Client.PutAsJsonAsync(
                Uri($"/v1/management/organizations/{databases[0].Organization}/projects/{sibling}/backup-destination"), Destination("both"), stop.Token);
            await AssertUnavailableAsync(duplicate, "customer backup configuration request limit reached");
            using var forbidden = await viewer.PutAsJsonAsync(firstRoute, Destination("both"), stop.Token);
            using var hidden = await outsider.PutAsJsonAsync(firstRoute, Destination("both"), stop.Token);
            await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(hidden.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            for (var index = 1; index < 4; index++)
            {
                pending.Add(api.Client.PutAsJsonAsync(Uri(databases[index].ProjectPath + "/backup-destination"), Destination("customer"), stop.Token));
            }

            await fourEntered.Task.WaitAsync(stop.Token);
            using var excess = await api.Client.PutAsJsonAsync(Uri(databases[4].ProjectPath + "/backup-destination"), Destination("customer"), stop.Token);
            await AssertUnavailableAsync(excess, "customer backup configuration request limit reached");
            await Assert.That(creates).IsEqualTo(4);
            release.TrySetResult();
            foreach (var request in pending)
            {
                using var response = await request.WaitAsync(stop.Token);
                response.EnsureSuccessStatusCode();
            }

            oci.BeforeSecretRequest = null;
            await SetAsync(api, databases[4], "customer");
            await SetAsync(api, databases[0], "both");
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
            await stop.CancelAsync();
            await ((Task)Task.WhenAll(pending)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var request in pending.Where(request => request.IsCompletedSuccessfully))
            {
                using var response = await request;
            }
        }
    }

    private static string MalformedVaultBody(string fault, bool bundle, string marker)
    {
        if (fault is "non-json" or "null" or "oversized")
        {
            return fault switch { "non-json" => marker, "null" => "null", _ => new string(' ', 64 * 1024) + marker };
        }

        if (!bundle)
        {
            return fault == "missing-id" ? $$"""{"marker":"{{marker}}"}""" : $$"""{"id":"{{marker}}/invalid"}""";
        }

        if (fault == "missing-content")
        {
            return $$"""{"marker":"{{marker}}"}""";
        }

        var version = fault == "wrong-version" ? 2 : 1;
        var access = fault == "invalid-credentials" ? "" : FakeOci.CustomerAccessKey;
        var credentials = $$"""{"version":{{version}},"accessKeyId":"{{access}}","secretKey":"{{FakeOci.CustomerSecretKey}}","marker":"{{marker}}"}""";
        var content = fault == "invalid-base64" ? marker + "!" : Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        var type = fault == "wrong-content-type" ? "PLAINTEXT" : "BASE64";
        return $$$"""{"marker":"{{{marker}}}","secretBundleContent":{"contentType":"{{{type}}}","content":"{{{content}}}"}}""";
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response, string message, string? marker = null)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var body = JsonDocument.Parse(json);
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("message").GetString()).IsEqualTo(message);
        await AssertPublicAsync(json);
        if (marker is not null)
        {
            await Assert.That(json.Contains(marker, StringComparison.Ordinal)).IsFalse();
        }
    }

    private static async Task<JsonElement> BackupRecordAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        var prefix = $"databases/{database.Id}/backup_status|current|";
        var rows = await DocumentFiles.ReadAsync(Path.Combine(api.Server.DataDirectory, "_system.db"));
        using var body = JsonDocument.Parse(rows.Single(row => row.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..]);
        return body.RootElement.Clone();
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

    private static void ManualShipping(FakeOci oci, IServiceCollection services)
    {
        oci.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static Dictionary<string, string> Destination(string mode) => new(StringComparer.Ordinal)
    {
        ["endpoint"] = FakeOci.CustomerEndpoint,
        ["region"] = "test-region-1",
        ["bucket"] = FakeOci.CustomerBucket,
        ["accessKeyId"] = FakeOci.CustomerAccessKey,
        ["secretKey"] = FakeOci.CustomerSecretKey,
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
        await Assert.That(body.RootElement.GetProperty("endpoint").GetString()!.TrimEnd('/')).IsEqualTo(FakeOci.CustomerEndpoint);
        await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("test-region-1");
        await Assert.That(body.RootElement.GetProperty("bucket").GetString()).IsEqualTo(FakeOci.CustomerBucket);
        await Assert.That(body.RootElement.GetProperty("mode").GetString()).IsEqualTo(mode);
        await Assert.That(body.RootElement.GetProperty("updatedAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.MinValue);
        await AssertPublicAsync(json);
    }

    private static async Task AssertPublicAsync(string json)
    {
        await Assert.That(json.Contains(FakeOci.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains(FakeOci.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("ocid1.vaultsecret", StringComparison.Ordinal)).IsFalse();
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

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
