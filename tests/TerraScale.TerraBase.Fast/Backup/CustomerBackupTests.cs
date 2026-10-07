using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupTests
{
    [Test]
    public async Task Both_destinations_ship_encrypted_snapshots_and_wal_and_restore_actual_documents()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
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
            using var window = await api.Client.GetAsync(Uri($"{database.Path}/point-in-time?destination={destination}"));
            window.EnsureSuccessStatusCode();
            var bounds = await window.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(bounds.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(snapshot);
            var latest = bounds.GetProperty("latest").GetDateTimeOffset();
            var restored = await RestoreAsync(api, database, latest, "latest-" + destination, destination);
            await AssertDocumentAsync(data, restored, "before", before);
            await AssertDocumentAsync(data, restored, "after", after);
            var earlier = await RestoreAsync(api, database, snapshot, "earlier-" + destination, destination);
            await AssertDocumentAsync(data, earlier, "before", before);
            using var absent = await data.GetAsync(Uri(ManagementApi.DocumentPath(earlier, "people", "after")));
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // Omitted destination selects platform for both window and restore.
        using var defaultWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time"));
        defaultWindow.EnsureSuccessStatusCode();
        var defaultBounds = await defaultWindow.Content.ReadFromJsonAsync<JsonElement>();
        var defaultRestore = await RestoreAsync(api, database, defaultBounds.GetProperty("latest").GetDateTimeOffset(), "default-restore");
        await AssertDocumentAsync(data, defaultRestore, "after", after);
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
    public async Task Customer_only_shipping_is_isolated_to_its_project_and_keeps_system_backups_on_platform()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
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
        using var platformWindow = await api.Client.GetAsync(Uri(customer.Path + "/point-in-time"));
        using var platformRestore = await api.Client.PostAsJsonAsync(Uri(customer.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), name = "no-platform" });
        using var siblingWindow = await api.Client.GetAsync(Uri(sibling.Path + "/point-in-time?destination=customer"));
        await Assert.That(platformWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(platformRestore.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(siblingWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var restored = await RestoreAsync(api, customer, time.GetUtcNow(), "customer-restored", "customer");
        await AssertDocumentAsync(data, restored, "only", text);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Credentials_live_in_vault_only_and_delete_resumes_platform_without_removing_customer_objects()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        using var unset = await api.Client.GetAsync(route);
        await Assert.That(unset.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await SetAsync(api, database, "customer");
        using var get = await api.Client.GetAsync(route);
        get.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await get.Content.ReadAsStringAsync(), "customer");
        await Assert.That(oci.Secrets.Count).IsEqualTo(1);
        using var secret = JsonDocument.Parse(oci.Secrets.Values.Single());
        await Assert.That(secret.RootElement.GetProperty("version").GetInt32()).IsEqualTo(1);
        await Assert.That(secret.RootElement.GetProperty("accessKeyId").GetString()).IsEqualTo(FakeOci.CustomerAccessKey);
        await Assert.That(secret.RootElement.GetProperty("secretKey").GetString()).IsEqualTo(FakeOci.CustomerSecretKey);
        var catalog = string.Join('\n', await DocumentFiles.ReadAsync(Path.Combine(api.Server.DataDirectory, "_system.db")));
        await Assert.That(catalog.Contains(FakeOci.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(catalog.Contains(FakeOci.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        foreach (var path in new[] { database.ProjectPath, database.Path, database.ProjectPath + "/databases" })
        {
            var response = await api.Client.GetStringAsync(Uri(path));
            await AssertPublicAsync(response);
        }

        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        var retained = oci.CustomerObjects.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal);
        using var deleted = await api.Client.DeleteAsync(route);
        using var gone = await api.Client.GetAsync(route);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await ShipAsync(api, time);
        await Assert.That(oci.CustomerObjects.Keys).IsEquivalentTo(retained.Keys);
        await Assert.That(retained.All(item => oci.CustomerObjects[item.Key].AsSpan().SequenceEqual(item.Value))).IsTrue();
        await Assert.That(oci.Objects.Keys.Any(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))).IsTrue();
        using var customerWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"));
        await Assert.That(customerWindow.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var restored = await RestoreAsync(api, database, time.GetUtcNow(), "platform-resumed");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Changing_a_destination_starts_a_new_generation_and_stops_platform_shipping_in_customer_mode()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "both");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var after = faker.Lorem.Sentence(12);
        await PutAsync(data, database, "before", before);
        await ShipAsync(api, time);
        var initial = Generations(oci.CustomerObjects, database);
        await Assert.That(initial.Count).IsEqualTo(1);
        var platformKeys = oci.Objects.Keys.Where(key => key.StartsWith(File(database) + "/", StringComparison.Ordinal)).ToArray();
        await SetAsync(api, database, "customer");
        await PutAsync(data, database, "after", after);
        await ShipAsync(api, time);
        var changed = Generations(oci.CustomerObjects, database);
        await Assert.That(changed.Count).IsEqualTo(2);
        await Assert.That(changed.IsSupersetOf(initial)).IsTrue();
        await Assert.That(oci.Objects.Keys.Where(key => key.StartsWith(File(database) + "/", StringComparison.Ordinal))).IsEquivalentTo(platformKeys);
        var restored = await RestoreAsync(api, database, time.GetUtcNow(), "changed-destination", "customer");
        await AssertDocumentAsync(data, restored, "before", before);
        await AssertDocumentAsync(data, restored, "after", after);
        await Assert.That(oci.Secrets.Count).IsEqualTo(2);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Invalid_destinations_and_unauthorized_callers_cannot_change_or_restore_customer_backups()
    {
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
            using var response = await api.Client.PutAsJsonAsync(route, invalid);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
        await Assert.That(oci.CustomerObjects.Count).IsEqualTo(0);
        await SetAsync(api, database, "both");
        var viewerId = await api.AddMemberAsync(api.Client, database.Organization, "viewer");
        using var viewer = api.As(viewerId);
        using var outsider = api.As(Guid.NewGuid().ToString());
        using var viewerPut = await viewer.PutAsJsonAsync(route, valid);
        using var viewerDelete = await viewer.DeleteAsync(route);
        using var viewerRestore = await viewer.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), destination = "customer" });
        await Assert.That(viewerPut.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(viewerDelete.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(viewerRestore.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var outsiderGet = await outsider.GetAsync(route);
        using var outsiderPut = await outsider.PutAsJsonAsync(route, valid);
        using var outsiderDelete = await outsider.DeleteAsync(route);
        using var outsiderWindow = await outsider.GetAsync(Uri(database.Path + "/point-in-time?destination=customer"));
        using var outsiderRestore = await outsider.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), destination = "customer" });
        foreach (var response in new[] { outsiderGet, outsiderPut, outsiderDelete, outsiderWindow, outsiderRestore })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using var invalidWindow = await api.Client.GetAsync(Uri(database.Path + "/point-in-time?destination=other"));
        using var invalidRestore = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), destination = "other" });
        await Assert.That(invalidWindow.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(invalidRestore.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var wrongCredentials = await api.Client.PutAsJsonAsync(route,
            new Dictionary<string, string>(valid, StringComparer.Ordinal) { ["secretKey"] = "wrong-secret" });
        await Assert.That(wrongCredentials.IsSuccessStatusCode).IsFalse();
        await Assert.That(oci.BadSignatures).IsGreaterThan(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(1);
        using var unchanged = await api.Client.GetAsync(route);
        unchanged.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await unchanged.Content.ReadAsStringAsync(), "both");
    }

    [Test]
    public async Task A_partial_mirror_upload_recovers_without_overlapping_segments_and_both_copies_restore()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
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
            var restored = await RestoreAsync(api, database, time.GetUtcNow(), "recovered-" + destination, destination);
            for (var index = 0; index < texts.Length; index++)
            {
                await AssertDocumentAsync(data, restored, names[index], texts[index]);
            }
        }

        var earlier = await RestoreAsync(api, database, partialAt, "partial-platform", "platform");
        await AssertDocumentAsync(data, earlier, "initial", texts[0]);
        await AssertDocumentAsync(data, earlier, "partial", texts[1]);
        using var absent = await data.GetAsync(Uri(ManagementApi.DocumentPath(earlier, "people", "recovered")));
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
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
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
        // Freeze this sibling project's platform history so legitimate retention cannot remove the sentinels.
        await SetAsync(api, foreign, "customer");
        await ShipAsync(api, time);
        for (var day = 0; day < 2; day++)
        {
            await PutAsync(data, database, "day-" + day, new Faker().Lorem.Sentence(12));
            time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            await ShipAsync(api, time);
        }

        var foreignReads = 0;
        var foreignDeletes = 0;
        var snapshotListings = 0;
        var segmentListings = 0;
        oci.ObjectRequested = (_, method, key) =>
        {
            if (preserved.ContainsKey(key))
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
            new { pointInTime = time.GetUtcNow(), name = "hostile-segment", destination = "customer" });
        if (rejectedSegment.IsSuccessStatusCode)
        {
            var body = await rejectedSegment.Content.ReadFromJsonAsync<JsonElement>();
            var restoredSegment = new ManagedDatabaseRef(database.Organization, database.Project, body.GetProperty("id").GetString()!, body.GetProperty("name").GetString()!);
            await AssertDocumentAsync(data, restoredSegment, "kept", text);
        }
        else
        {
            await AssertPublicAsync(await rejectedSegment.Content.ReadAsStringAsync());
        }

        // A restore also receives a foreign snapshot alone: rejecting it and ignoring it are both safe.
        oci.CustomerListingOverride = (prefix, keys) => prefix == File(database) + "/snapshots/" ? [foreignSnapshot] : keys;
        using var rejected = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = time.GetUtcNow(), name = "hostile-listing", destination = "customer" });
        await Assert.That(rejected.IsSuccessStatusCode).IsFalse();
        await AssertPublicAsync(await rejected.Content.ReadAsStringAsync());
        await Assert.That(snapshotListings).IsGreaterThan(0);
        await Assert.That(segmentListings).IsGreaterThan(0);
        await Assert.That(foreignReads).IsEqualTo(0);
        await Assert.That(foreignDeletes).IsEqualTo(0);
        await Assert.That(preserved.All(item => oci.Objects.TryGetValue(item.Key, out var bytes) && bytes.AsSpan().SequenceEqual(item.Value))).IsTrue();
        oci.CustomerListingOverride = null;
        oci.ObjectRequested = null;
        var restored = await RestoreAsync(api, database, time.GetUtcNow(), "safe-listing", "customer");
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

            var platformRestore = await RestoreAsync(api, sibling, time.GetUtcNow(), "unstalled-platform", "platform");
            await AssertDocumentAsync(data, platformRestore, "initial", texts[0]);
            for (var index = 0; index < names.Length; index++)
            {
                await AssertDocumentAsync(data, platformRestore, names[index], texts[index + 1]);
            }

            oci.EmptyCustomerPages = null;
            var customerRestore = await RestoreAsync(api, customer, time.GetUtcNow(), "unstalled-customer", "customer");
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
                var restored = await RestoreAsync(api, database, time.GetUtcNow(), "deadline-" + destination, destination);
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
            var restored = await RestoreAsync(api, database, time.GetUtcNow(), "cancelled-" + destination, destination);
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
    public async Task Customer_restore_rejects_wrapped_key_bodies_larger_than_sixteen_kibibytes()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetAsync(api, database, "customer");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text);
        await ShipAsync(api, time);
        var key = oci.CustomerObjects.Single(item => item.Key.StartsWith($"v1/keys/{database.Organization}/{database.Project}/", StringComparison.Ordinal));
        // Whitespace still decodes to the original key: without a body bound, this hostile restore succeeds.
        var oversized = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(key.Value) + new string(' ', 16 * 1024));
        var keyReads = 0;
        oci.CustomerReadOverride = requested =>
        {
            if (requested != key.Key)
            {
                return null;
            }

            Interlocked.Increment(ref keyReads);
            return oversized;
        };
        using var rejected = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = time.GetUtcNow(), name = "oversized-key", destination = "customer" });
        await Assert.That(keyReads).IsGreaterThan(0);
        await Assert.That(rejected.IsSuccessStatusCode).IsFalse();
        var error = await rejected.Content.ReadAsStringAsync();
        await AssertPublicAsync(error);
        await Assert.That(error.Contains(Encoding.ASCII.GetString(key.Value), StringComparison.Ordinal)).IsFalse();
        await AssertDocumentAsync(data, database, "kept", text);
        oci.CustomerReadOverride = null;
        var restored = await RestoreAsync(api, database, time.GetUtcNow(), "bounded-key", "customer");
        await AssertDocumentAsync(data, restored, "kept", text);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Retention_trims_platform_snapshots_from_before_customer_attachment_and_retries_platform_locks_independently()
    {
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, oci.Route, time);
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
            var restored = await RestoreAsync(api, database, time.GetUtcNow(), "retained-" + destination, destination);
            await AssertDocumentAsync(data, restored, "kept", text);
        }

        await Assert.That(oci.BadSignatures).IsEqualTo(0);
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
        // Only the test drives shipping. Keep the real service and its shutdown cleanup, without timer races.
        var host = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType is { } type && typeof(IContinuousBackup).IsAssignableFrom(type));
        services[services.IndexOf(host)] = ServiceDescriptor.Singleton<IHostedService, ManualBackupHost>();
    }

    private sealed class ManualBackupHost(IContinuousBackup backup) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => ((IHostedService)backup).StopAsync(cancellationToken);
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

    private static async Task SetAsync(ManagementApi api, ManagedDatabaseRef database, string mode)
    {
        using var response = await api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination(mode));
        response.EnsureSuccessStatusCode();
        await AssertDestinationAsync(await response.Content.ReadAsStringAsync(), mode);
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

    private static async Task ShipAsync(ManagementApi api, FakeTimeProvider time, CancellationToken cancellationToken = default)
    {
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text)
    {
        using var response = await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", name)), new { text });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ManagedDatabaseRef> RestoreAsync(ManagementApi api, ManagedDatabaseRef database, DateTimeOffset at, string name, string? destination = null)
    {
        var body = new Dictionary<string, object>(StringComparer.Ordinal) { ["pointInTime"] = at, ["name"] = name };
        if (destination is not null)
        {
            body["destination"] = destination;
        }

        using var response = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), body);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var restored = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(restored.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        return new(database.Organization, database.Project, restored.GetProperty("id").GetString()!, restored.GetProperty("name").GetString()!);
    }

    private static async Task AssertDocumentAsync(HttpClient data, ManagedDatabaseRef database, string name, string text)
    {
        using var response = await data.GetAsync(Uri(ManagementApi.DocumentPath(database, "people", name)));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    private static HashSet<string> Generations(IReadOnlyDictionary<string, byte[]> objects, ManagedDatabaseRef database) =>
        objects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))
            .Select(key => key.Split('/')[^1].Split('-')[1]).ToHashSet(StringComparer.Ordinal);

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
