using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CatalogRestoreSafetyTests
{
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    public async Task A_new_region_can_verify_a_revocation_from_a_peer_that_restarted_in_a_new_epoch()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        await using var gru = new Machine("gru", bucket, members);
        await gru.StartAsync();
        var organization = await gru.Api.CreateOrganizationAsync("rollout");
        var project = await gru.Api.CreateProjectAsync(organization, "project");
        var path = ManagementApi.Uri("/admin/api-keys");
        using var created = await gru.Api.Server.Client.PostAsJsonAsync(path, new { name = "revoked before rollout", scopes = ReadScopes, organizationId = organization, projectId = project }, token);
        created.EnsureSuccessStatusCode();
        var key = await created.Content.ReadFromJsonAsync<JsonElement>(token);
        var id = key.GetProperty("id").GetString()!;
        var secret = key.GetProperty("secret").GetString()!;
        using var revoked = await gru.Api.Server.Client.DeleteAsync(ManagementApi.Uri($"/admin/api-keys/{id}"), token);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var before = gru.Api.Server.Services.GetRequiredService<LocalReplica>().Id;
        var source = await gru.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        long anchoredCut;
        await using (var read = await source.BeginTransactionAsync(write: false, token))
        {
            anchoredCut = (await read.GetVersionAsync(CatalogGate.Revocations, "gru", token))!.Value.Version;
        }
        await gru.StopAsync();
        gru.MarkUnclean();
        await gru.StartAsync();
        _ = await ((CatalogGate)gru.Gate).VerifyAsync(token);
        var after = gru.Api.Server.Services.GetRequiredService<LocalReplica>().Id;
        await Assert.That(after.Member).IsEqualTo(before.Member);
        await Assert.That(after.Epoch).IsEqualTo((ushort)(before.Epoch + 1));
        await Assert.That(gru.Gate.Trusted).IsTrue().Because("the same durable catalog still holds its authenticated external anchor");
        source = await gru.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using (var read = await source.BeginTransactionAsync(write: false, token))
        {
            var applied = await read.GetAppliedAsync(token);
            await Assert.That(applied[before].Value).IsEqualTo(unchecked((ulong)anchoredCut))
                .Because("only the authenticated cut is proved, never the old epoch's later tail");
        }

        // No previous catalog, anchor, or applied vector exists on fra. Its peer's anchor names the previous epoch.
        await using var fra = new Machine("fra", bucket, members);
        await fra.StartAsync();
        _ = await ((CatalogGate)fra.Gate).VerifyAsync(token);
        await Assert.That(fra.Gate.IsOpen).IsTrue().Because("a whole peer snapshot must carry its proven old-epoch revocation cut");
        await Assert.That(await fra.Api.Server.Services.GetRequiredService<IApiKeyStore>().FindAsync(secret, token) is NotFound).IsTrue();
        using var tls = new ClientTls();
        using var client = tls.CreateClient(fra.Api.Server.ClientAddress!, secret);
        using var response = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/missing/collections"), token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task A_closed_gate_reports_an_external_anchor_read_in_progress_and_clears_it_on_cancellation()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        await using var machine = new Machine("gru", bucket, new FakeMemberStore());
        await machine.StartAsync();
        await Assert.That(machine.Gate.IsOpen).IsTrue();
        await machine.StopAsync();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bucket.BeforeList = async (prefix, cancellationToken) =>
        {
            if (prefix == "catalog-anchor/")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        // The initial verification has a bounded start budget; the fake clock prevents a concurrent background pass.
        await machine.StartAsync();
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var verify = ((CatalogGate)machine.Gate).VerifyAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(token);
            await Assert.That(machine.Gate.IsOpen).IsFalse();
            await Assert.That(machine.Gate.Waiting.Contains("listing external catalog revocation anchors")).IsTrue();
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                _ = await verify;
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
            }
        }
        await Assert.That(machine.Gate.IsOpen).IsFalse();
        await Assert.That(machine.Gate.Waiting.Contains("listing external catalog revocation anchors")).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Restored_majorities_never_vouch_for_a_revocation_missing_from_all_their_catalogs(int stale)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        await using var gru = new Machine("gru", bucket, members);
        await using var sjc = new Machine("sjc", bucket, members);
        await using var fra = new Machine("fra", bucket, members);
        Machine[] machines = [gru, sjc, fra];
        foreach (var machine in machines)
        {
            await machine.StartAsync();
        }
        foreach (var machine in machines)
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(machine.Gate.IsOpen));
        }
        var organization = await gru.Api.CreateOrganizationAsync("restore-majority");
        var project = await gru.Api.CreateProjectAsync(organization, "project");
        using var created = await gru.Api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new { name = "restore-majority", scopes = ReadScopes, organizationId = organization, projectId = project }, token);
        created.EnsureSuccessStatusCode();
        var key = await created.Content.ReadFromJsonAsync<JsonElement>(token);
        var secret = key.GetProperty("secret").GetString()!;
        var id = key.GetProperty("id").GetString()!;
        foreach (var machine in machines)
        {
            if (machine != gru)
            {
                _ = await machine.Api.Server.Services.GetRequiredService<AntiEntropy>().PullAsync(ChangeGroups.System,
                    new ClusterMember("gru", gru.Api.Server.BaseAddress.ToString(), "gru", ""), token);
            }
            await Assert.That(await machine.Api.Server.Services.GetRequiredService<IApiKeyStore>().FindAsync(secret, token) is ApiKey).IsTrue();
            await Assert.That(await machine.Gate.TryAnchorAsync(token)).IsTrue();
            await machine.SnapshotAsync(token);
        }

        // The other two anchors stay at the snapshot. Their matching local anchors cannot prove that they hold gru's
        // later, acknowledged revocation. In particular two restored peers must not form a stale serving majority.
        await sjc.StopAsync();
        await fra.StopAsync();
        using var revoked = await gru.Api.Server.Client.DeleteAsync(ManagementApi.Uri($"/admin/api-keys/{id}"), token);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await gru.StopAsync();
        for (var index = 0; index < machines.Length; index++)
        {
            if (index < stale)
            {
                machines[index].Restore();
            }
            await machines[index].StartAsync();
        }
        // Exercise the actual verification path after all listeners have new addresses, without advancing background clocks.
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var machine in machines)
            {
                _ = await ((CatalogGate)machine.Gate).VerifyAsync(token);
            }
        }
        foreach (var machine in machines)
        {
            await Assert.That(machine.Gate.IsOpen).IsEqualTo(stale == 0);
            if (machine.Gate.IsOpen)
            {
                await Assert.That(await machine.Api.Server.Services.GetRequiredService<IApiKeyStore>().FindAsync(secret, token) is NotFound).IsTrue();
            }
            else
            {
                var system = await machine.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
                await using var read = await system.BeginTransactionAsync(write: false, token);
                await Assert.That(await read.GetAsync("api_keys", id, token)).IsNotNull()
                    .Because("the readable restored catalog still holds the revoked key; the gate must refuse to authorize it");
            }
            using var tls = new ClientTls();
            using var client = tls.CreateClient(machine.Api.Server.ClientAddress!, secret);
            using var response = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/missing/collections"), token);
            await Assert.That(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable).IsTrue()
                .Because("a restored catalog can contain the old key, but cannot authorize it");
        }
    }

    private sealed class Machine(string region, FakeS3 bucket, FakeMemberStore members) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("catalog-restore-majority-").FullName;
        private readonly FakeTimeProvider _time = new();
        private ManagementApi? _api;
        public ManagementApi Api => _api ?? throw new InvalidOperationException("machine stopped");
        public ICatalogGate Gate => Api.Server.Services.GetRequiredService<ICatalogGate>();
        public void MarkUnclean() => _ = ReplicaIdentityFile.Start(_directory, region);
        public async Task StartAsync() => _api = await ManagementApi.StartAsync(settings =>
        {
            bucket.Configure(settings);
            settings["node-name"] = region;
            settings["region"] = region;
            settings["data-directory"] = _directory;
        }, services =>
        {
            bucket.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddSingleton<IMemberDirectory>(members);
        }, _time);
        public async Task SnapshotAsync(CancellationToken token)
        {
            Directory.CreateDirectory(Path.Combine(_directory, ".restore-drill"));
            await using var tap = await Api.Server.Services.GetRequiredService<IWalBackup>().OpenTapAsync(null, token);
            _ = await tap!.SnapshotAsync(Stream.Null, Path.Combine(_directory, ".restore-drill", "before.db"), token);
        }
        public void Restore()
        {
            File.Copy(Path.Combine(_directory, ".restore-drill", "before.db"), Path.Combine(_directory, "_system.db"), overwrite: true);
            // Each encrypted snapshot has its own data key: restore its wrapped key with its ciphertext.
            File.Copy(Path.Combine(_directory, ".restore-drill", "before.db" + DatabaseKeys.Suffix),
                Path.Combine(_directory, "_system.db" + DatabaseKeys.Suffix), overwrite: true);
            File.Delete(Path.Combine(_directory, "_system.db-wal"));
            File.Delete(Path.Combine(_directory, "_system.db-shm"));
        }
        public async Task StopAsync()
        {
            if (_api is { } api)
            {
                await api.DisposeAsync();
                _api = null;
            }
        }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
