using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

// Two real regions over peer mTLS. The only fault is CatalogDelivery: it holds one project's catalog name decisions at
// each receiver until healed. HTTP admission, local transactions, durable ACKs and convergence stay the production paths.
public sealed partial class DatabaseRenameRaceTests
{
    private const string Scopes = "databases:read databases:write documents:read documents:write";

    private sealed record Regions(ManagementApi Gru, ManagementApi Sjc, CatalogDelivery Delivery) : IAsyncDisposable
    {
        public ManagementApi[] All => [Gru, Sjc];

        public async ValueTask DisposeAsync()
        {
            Delivery.Release();
            await Sjc.DisposeAsync();
            await Gru.DisposeAsync();
        }
    }

    // Both regions, peered and catalog-replicated, with one bound project whose first database `name` gru creates.
    private static async Task<(Regions Regions, ManagedDatabaseRef First)> StartAsync(string name)
    {
        var members = new FakeMemberStore();
        var delivery = new CatalogDelivery();
        var gru = await StartRegionAsync(members, delivery, "gru");
        ManagementApi? sjc = null;
        try
        {
            var peer = await StartRegionAsync(members, delivery, "sjc", gru.User);
            sjc = peer;
            await AwaitPeersAsync(gru, peer);
            var first = await gru.CreateManagedDatabaseAsync(name);
            await UntilAsync(async () => await ReadCatalogAsync(peer, $"projects/{first.Project}/databases", first.Id) is not null);
            peer.Issuer.RegisterProject(first.Project, first.Organization);
            return (new Regions(gru, peer, delivery), first);
        }
        catch
        {
            if (sjc is not null)
            {
                await sjc.DisposeAsync();
            }

            await gru.DisposeAsync();
            throw;
        }
    }

    private static Task<ManagementApi> StartRegionAsync(FakeMemberStore members, CatalogDelivery delivery, string region, string? user = null) =>
        ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = Guid.NewGuid().ToString("N")[..14];
        }, services =>
        {
            services.AddSingleton<IMemberDirectory>(members);
            services.AddSingleton<StorageChangeGroupSink>();
            services.AddSingleton<IChangeGroupSink>(provider => new DelayedSink(provider.GetRequiredService<StorageChangeGroupSink>(), delivery));
        }, userId: user);

    private static async Task AwaitPeersAsync(params ManagementApi[] regions)
    {
        foreach (var region in regions)
        {
            await TerraBaseTestServer.EventuallyAsync(async token =>
                (await region.Server.Services.GetRequiredService<IRemoteDurability>().OtherRegionsAsync(token)).Count == regions.Length - 1,
                timeout: TimeSpan.FromSeconds(30));
        }

        await ReplicatedCatalogAsync([.. regions.Select(region => region.Server)]);
    }

    // Every peer durably ACKed each server's own catalog groups up to its head: own-origin groups only (a joined or repaired
    // row is never echoed, so the raw head is no ACK target), in the incarnation in place, with the captured prefix retained.
    private static async Task ReplicatedCatalogAsync(params TerraBaseTestServer[] servers)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        foreach (var server in servers)
        {
            var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
            var source = server.Services.GetRequiredService<IChangeGroupSource>();
            var physicalHead = await source.HeadAsync(ChangeGroups.System, store.Incarnation, token)
                ?? throw new InvalidOperationException("the catalog refused its own incarnation");
            long target = 0;
            var complete = false;
            while (!complete && target < physicalHead)
            {
                var page = await source.ReadAsync(ChangeGroups.System, store.Incarnation, target, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, token);
                if (page is null || page.Incarnation != store.Incarnation)
                {
                    throw new InvalidOperationException("the catalog changed incarnation while capturing replication");
                }

                complete = page.Groups.Count == 0;
                foreach (var group in page.Groups)
                {
                    if (group.ChangeId > physicalHead)
                    {
                        complete = true;
                        break;
                    }

                    target = group.ChangeId;
                }
            }

            if (await source.RetainsAsync(ChangeGroups.System, store.Incarnation, 0, token) != true)
            {
                throw new InvalidOperationException("the fresh catalog fixture no longer retains its captured prefix");
            }

            // Empty peer or cursor sets never count as completion.
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                var status = server.Services.GetRequiredService<IReplicationStatus>().Snapshot();
                return Task.FromResult(status.Initialized && status.OmittedDatabases == 0 && status.Peers.Count == servers.Length - 1
                    && status.Peers.All(peer => peer.Databases.Any(database => database.DatabaseId == ChangeGroups.System.DatabaseId
                        && database.Incarnation == store.Incarnation && database.Acknowledged >= target)));
            }, timeout: TimeSpan.FromSeconds(30));
        }
    }

    // Places the database everywhere, writes its marker document and takes a backup; returns the backup list and policy.
    private static async Task<(string Backups, string Policy)> PrepareAsync(ManagementApi home, ManagedDatabaseRef database)
    {
        using var client = Client(home, database.Project);
        using (var placed = await client.PatchAsJsonAsync(Uri(ById(database).Data), new { allRegions = true }))
        {
            await Assert.That(placed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await placed.Content.ReadAsStringAsync());
        }

        await PopulateAsync(home, database);
        using (var backup = await home.Client.PostAsync(Uri(database.Path + "/backups"), content: null))
        {
            await Assert.That(backup.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await backup.Content.ReadAsStringAsync());
        }

        return (await home.Client.GetStringAsync(Uri(database.Path + "/backups")), await home.Client.GetStringAsync(Uri(database.Path + "/backup-policy")));
    }

    private static async Task PopulateAsync(ManagementApi home, ManagedDatabaseRef database)
    {
        using var client = Client(home, database.Project);
        await ManagementApi.CreateCollectionAsync(client, ById(database), "items");
        using var response = await client.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(ById(database), "items", "saved")), new { owner = database.Id });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> ReadCatalogAsync(ManagementApi api, string collection, string id)
    {
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var read = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        return await read.GetAsync(collection, id, CancellationToken.None) is { } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
    }

    // The database's name in api's own catalog, and whether its own name index entry points at it: a committed local decision.
    private static async Task<bool> NamedLocallyAsync(ManagementApi api, string project, string id, string name) =>
        await ReadCatalogAsync(api, $"projects/{project}/database_names", name) == $"\"{id}\""
        && await ReadCatalogAsync(api, $"projects/{project}/databases", id) is { } record
        && JsonElement.Parse(record).GetProperty("name").GetString() == name;

    // The management body of the database, read from api by ULID.
    private static async Task<JsonElement> BodyAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        using var client = Client(api, database.Project);
        using var response = await client.GetAsync(Uri(ById(database).Data));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string? Field(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task AssertMissingAsync(ManagementApi[] regions, string project, params string[] references)
    {
        foreach (var region in regions)
        {
            using var client = Client(region, project);
            foreach (var reference in references)
            {
                using var missing = await client.GetAsync(Uri($"/v1/projects/{project}/databases/{reference}"));
                await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(reference);
            }
        }
    }

    // In every region: the name index holds exactly one entry per live database, under the name its record carries, every
    // region holds the same labels, `existing` are live and `deleted` are gone. Then, over HTTP in each region, every existing
    // database answers by ULID and by name with its file and marker document, also through a key restricted to its ULID, and
    // has no purge authority or cleanup intent. Answers the final labels by ULID.
    private static async Task<Dictionary<string, string>> AssertConvergedAsync(
        Regions regions, string project, ManagedDatabaseRef[] existing, ManagedDatabaseRef[]? deleted = null)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var servers = regions.All.Select(region => region.Server).ToArray();
        Dictionary<string, string> labels = [];
        await UntilAsync(async () =>
        {
            await ReplicatedCatalogAsync(servers);
            foreach (var region in regions.All)
            {
                await region.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(token);
            }

            await ReplicatedCatalogAsync(servers);
            string? expected = null;
            foreach (var region in regions.All)
            {
                var catalog = await region.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
                await using var read = await catalog.BeginTransactionAsync(write: false, token);
                var databases = await read.ListAsync($"projects/{project}/databases", null, 100, 1024 * 1024, token);
                var names = await read.ListAsync($"projects/{project}/database_names", null, 100, 1024 * 1024, token);
                labels = databases.Documents.ToDictionary(document => document.Id, document =>
                    EntityPayload.Read(document, TerraBaseJsonContext.Default.ManagedDatabase)!.Name, StringComparer.Ordinal);
                if (databases.HasMore || names.HasMore || labels.Values.Distinct(StringComparer.Ordinal).Count() != labels.Count
                    || existing.Any(database => !labels.ContainsKey(database.Id)) || (deleted ?? []).Any(database => labels.ContainsKey(database.Id))
                    || names.Documents.Count != labels.Count
                    || names.Documents.Any(document => !labels.TryGetValue(JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.String)!, out var label)
                        || label != document.Id))
                {
                    return false;
                }

                var signature = string.Join(';', labels.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
                if (expected is not null && expected != signature)
                {
                    return false;
                }

                expected = signature;
            }

            return true;
        });

        foreach (var region in regions.All)
        {
            using var client = Client(region, project);
            foreach (var database in existing)
            {
                using var restricted = Client(region, project, database.Id);
                var marker = ManagementApi.DocumentPath(ById(database), "items", "saved");
                // The catalog can finish before a newly placed replica has opened and refilled its data file.
                await UntilAsync(async () =>
                {
                    using var ready = await restricted.GetAsync(Uri(marker), token);
                    return ready.StatusCode == HttpStatusCode.OK;
                });
                await Assert.That(File.Exists(Path.Combine(region.Server.DataDirectory, database.Organization, project, database.Id + ".db"))).IsTrue();
                foreach (var reference in new[] { database.Id, labels[database.Id] })
                {
                    using var saved = await client.GetAsync(Uri(ManagementApi.DocumentPath(database with { Name = reference }, "items", "saved")), token);
                    await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(reference);
                    var document = await saved.Content.ReadFromJsonAsync<JsonElement>(token);
                    await Assert.That(document.GetProperty("document").GetProperty("owner").GetString()).IsEqualTo(database.Id);
                }

                await Assert.That(await ReadCatalogAsync(region, DatabasePurge.Collection, database.Id)).IsNull();
                await Assert.That(await ReadCatalogAsync(region, "pending_database_cleanup", database.Id)).IsNull();
            }

            foreach (var database in deleted ?? [])
            {
                await AssertMissingAsync([region], project, database.Id);
                using var data = await client.GetAsync(Uri(ManagementApi.DocumentPath(ById(database), "items", "saved")), token);
                await Assert.That(data.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
        }

        return labels;
    }

    // What convergence names the loser of `name` first (docs/terrabase/database-name-conflicts.md), when that name is free.
    private static string ConflictName(string name, string id) => $"{name}-conflict-{id[^6..].ToLowerInvariant()}";

    private static ManagedDatabaseRef ById(ManagedDatabaseRef database) => database with { Name = database.Id };

    private static HttpClient Client(ManagementApi api, string project, string? database = null) => api.WithToken(api.Issuer.Mint(api.User, token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = Scopes;
        if (database is not null)
        {
            token.Claims["database"] = database;
        }
    }));

    private static Task UntilAsync(Func<Task<bool>> condition) => TerraBaseTestServer.EventuallyAsync(_ => condition(), timeout: TimeSpan.FromSeconds(30));

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    // Holds every catalog batch that touches the armed project's databases or name index, at each receiver, until healed.
    private sealed class CatalogDelivery
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _project;

        public void Arm(string project) => Volatile.Write(ref _project, project);

        public void Release() => _released.TrySetResult();

        public Task BeforeAsync(TenantContext database, IEnumerable<string> collections, CancellationToken cancellationToken) =>
            database == ChangeGroups.System && Volatile.Read(ref _project) is { } project
                && collections.Any(collection => collection == $"projects/{project}/databases" || collection == $"projects/{project}/database_names")
                ? _released.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    private sealed class DelayedSink(IChangeGroupSink inner, CatalogDelivery delivery) : IChangeGroupSink
    {
        public async ValueTask<Hlc?> ApplyAsync(
            TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            await delivery.BeforeAsync(database, groups.SelectMany(group => group.Deltas).Select(delta => delta.Collection), cancellationToken);
            return await inner.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
        }

        public async ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken)
        {
            await delivery.BeforeAsync(database, states.Select(state => state.Collection), cancellationToken);
            return await inner.RepairAsync(database, incarnation, states, cancellationToken);
        }

        public ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            inner.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
    }
}
