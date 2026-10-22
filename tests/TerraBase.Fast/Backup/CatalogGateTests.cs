using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Errors;
using TerraBase.Fast.Replication;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

[Category("restore-drill")]
public sealed class CatalogGateTests
{
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    [Skip("Quarantine: owner R5; deadline 2026-10-23 20:00Z; root-cause fix and removal in #364 (Q-MAIN-GREEN).")]
    [Arguments(StatusCode.Cancelled)]
    [Arguments(StatusCode.Internal)]
    public async Task Catalog_gate_shutdown_normalizes_only_genuine_RPC_cancellation(StatusCode status)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider();
        var directory = new ShutdownDirectory(status);
        var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IMemberDirectory>(directory);
            // Only the catalog gate may signal that its discovery call is waiting for shutdown.
            services.AddSingleton<IMembership>(provider => new StaticMembership(provider.GetRequiredService<TerraBaseOptions>()));
        });
        var gate = (CatalogGate)server.Services.GetRequiredService<ICatalogGate>();
        try
        {
            await Assert.That(gate.IsOpen).IsFalse();
            directory.Armed = true;
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                return Task.FromResult(directory.Entered.Task.IsCompleted);
            });
            var shutdown = server.DisposeAsync().AsTask();
            if (status == StatusCode.Cancelled)
            {
                await shutdown.WaitAsync(token);
                await Assert.That(gate.ExecuteTask!.IsCanceled).IsTrue();
            }
            else
            {
                var error = await Assert.That(async () => await shutdown.WaitAsync(token)).Throws<RpcException>();
                await Assert.That(error!.StatusCode).IsEqualTo(status);
                await Assert.That(gate.ExecuteTask!.IsFaulted).IsTrue();
            }
        }
        finally
        {
            try
            {
                await server.DisposeAsync();
            }
            catch (RpcException) when (status == StatusCode.Internal && gate.ExecuteTask!.IsFaulted)
            {
            }
        }
    }

    private sealed class ShutdownDirectory(StatusCode status) : IMemberDirectory
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
        {
            if (!Armed)
            {
                throw new IOException("catalog discovery is unavailable");
            }

            _ = Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new RpcException(new Status(status, "discovery ended during shutdown"));
            }

            return [];
        }
    }

    [Test]
    public async Task A_fresh_replacement_pulls_a_trusted_catalog_despite_a_destroyed_historical_member()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var members = new FakeMemberStore();
        await using var healthy = await ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            settings["node-name"] = "healthy";
            settings["region"] = "gru";
        }, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddSingleton<IMemberDirectory>(members);
        });
        var organization = await healthy.CreateOrganizationAsync("restore-drill");
        var catalog = await healthy.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using (var transaction = await catalog.BeginTransactionAsync(write: true, token))
        {
            _ = await transaction.PutAsync(CatalogGate.Members, "destroyed", "{\"region\":\"sjc\"}"u8.ToArray(), token);
            _ = await transaction.CommitAsync(token);
        }
        await Assert.That(await healthy.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(token)).IsTrue();
        members.Join(new ClusterMember("healthy", healthy.Server.BaseAddress.ToString(), "gru", ""));

        await using var replacement = await TerraBaseTestServer.StartAsync(settings =>
        {
            s3.Configure(settings);
            settings["node-name"] = "replacement";
            settings["region"] = "sjc";
        }, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddSingleton<IMemberDirectory>(members);
        });
        await Assert.That(replacement.Services.GetRequiredService<ICatalogGate>().IsOpen).IsTrue();

        // The trust probe reached healthy's internal listener, the only peer it has, and crossed the credential check.
        using (var scraper = new HttpClient { BaseAddress = healthy.Server.MetricsAddress, Timeout = Timeout.InfiniteTimeSpan })
        {
            var (matched, unmatched) = await PeerCredentialObservationTests.CountsAsync(scraper);
            await Assert.That(matched).IsGreaterThan(0);
            await Assert.That(unmatched).IsEqualTo(0);
        }

        var recovered = await replacement.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await recovered.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("organizations", organization, token)).IsNotNull();
        await Assert.That(await read.GetAsync(CatalogGate.Members, "destroyed", token)).IsNotNull();
        await Assert.That(s3.InconsistentAnchorReads).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_current_catalog_reopens_from_its_own_anchor_without_an_operator(bool directoryBacked)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-current-catalog-").FullName;
        var s3 = new FakeS3();
        var members = new FakeMemberStore();
        try
        {
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
                if (directoryBacked)
                {
                    services.AddSingleton<IMemberDirectory>(members);
                }
            }))
            {
                await api.CreateOrganizationAsync("restart-control");
                await Assert.That(await api.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(token)).IsTrue();
            }

            await using var reopened = await TerraBaseTestServer.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
                if (directoryBacked)
                {
                    services.AddSingleton<IMemberDirectory>(members);
                }
            });
            var gate = reopened.Services.GetRequiredService<ICatalogGate>();
            if (directoryBacked)
            {
                // The harness lists a machine only after its listeners start; the gate needs that discovery proof.
                _ = await ((CatalogGate)gate).VerifyAsync(token);
            }

            await Assert.That(gate.IsOpen).IsTrue();
            await Assert.That(gate.Trusted).IsTrue();
            using var ready = await reopened.Client.GetAsync(ManagementApi.Uri("/readyz"), token);
            await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(s3.InconsistentAnchorReads).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_anchor_failure_refuses_destructive_management_but_not_tenant_writes(bool commitEnvelope)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var host = await BackupShippingHost.StartAsync();
        host.S3.FailPut = key => key.StartsWith("catalog-anchor/", StringComparison.Ordinal);
        await host.PutAsync("people", "bucket-independent", "tenant writes continue", token);
        var request = new
        {
            id = EntityIds.New().ToString(),
            ops = new[] { new { op = "delete_database", organization = host.Database.Organization, project = host.Database.Project, database = host.Database.Id } },
        };
        using var refused = commitEnvelope
            ? await host.Api.Client.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), request, token)
            : await host.Api.Client.DeleteAsync(ManagementApi.Uri(host.Database.Path), token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var error = await refused.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("catalog_anchor_unavailable");

        host.S3.FailPut = _ => false;
        if (commitEnvelope)
        {
            using var replay = await host.Api.Client.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), request, token);
            replay.EnsureSuccessStatusCode();
            await Assert.That(replay.Headers.Contains(CommitEndpoint.ReplayedHeader)).IsTrue();
        }
        else
        {
            await Assert.That(await host.Api.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(token)).IsTrue();
        }
    }

    [Test]
    [Arguments("present")]
    [Arguments("missing")]
    [Arguments("tampered")]
    [Arguments("replayed")]
    public async Task An_old_system_catalog_cannot_revive_a_revoked_key(string anchor)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-restore-gate-").FullName;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider();
        try
        {
            string secret;
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
            }, time))
            {
                var database = await api.CreateManagedDatabaseAsync();
                using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/projects/{database.Project}/api-keys"),
                    new { name = "restore-drill", scopes = ReadScopes }, token);
                created.EnsureSuccessStatusCode();
                var key = await created.Content.ReadFromJsonAsync<JsonElement>(token);
                secret = key.GetProperty("secret").GetString()!;
                var id = key.GetProperty("key").GetProperty("id").GetString()!;
                Directory.CreateDirectory(Path.Combine(directory, ".restore-drill"));
                await using (var tap = await api.Server.Services.GetRequiredService<IWalBackup>().OpenTapAsync(null, token))
                {
                    using var log = new MemoryStream();
                    await tap!.SnapshotAsync(log, Path.Combine(directory, ".restore-drill", "pre-revocation.db"), token);
                    if (anchor == "replayed")
                    {
                        log.SetLength(0);
                        await api.CreateOrganizationAsync("After the snapshot");
                        await tap.ReadAsync(log, token);
                        log.Position = 0;
                        var backup = api.Server.Services.GetRequiredService<IWalBackup>();
                        await Assert.That(await backup.RollForwardAsync(Path.Combine(directory, ".restore-drill", "pre-revocation.db"), SegmentsAsync(log), token)).IsTrue();
                    }
                }

                using var revoked = await api.Client.DeleteAsync(ManagementApi.Uri($"/v1/management/organizations/{database.Organization}/projects/{database.Project}/api-keys/{id}"), token);
                await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                await Assert.That(await api.Server.Services.GetRequiredService<IApiKeyStore>().FindAsync(secret, token) is NotFound).IsTrue();
                await Assert.That(s3.Objects.ContainsKey("catalog-anchor/local")).IsTrue();
                if (anchor == "missing")
                {
                    await api.Server.Services.GetRequiredService<TerraBase.Features.Backup.IObjectStore>().DeleteAsync("catalog-anchor/local", token);
                }
                else if (anchor == "tampered")
                {
                    s3.Objects["catalog-anchor/local"][^1] ^= 0xff;
                }
            }

            // The server is fully stopped; its local fixture files are owned by this test alone.
            File.Move(Path.Combine(directory, ".restore-drill", "pre-revocation.db"), Path.Combine(directory, "_system.db"), overwrite: true);
            File.Delete(Path.Combine(directory, "_system.db-wal"));
            File.Delete(Path.Combine(directory, "_system.db-shm"));

            await using var restored = await TerraBaseTestServer.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
                services.AddSingleton<TimeProvider>(time);
            });
            // Prove this is the stale, pre-revocation fixture rather than a discarded/empty catalog.
            await Assert.That(await restored.Services.GetRequiredService<IApiKeyStore>().FindAsync(secret, token) is ApiKey).IsTrue();
            using var tls = new ClientTls();
            using var data = tls.CreateClient(restored.ClientAddress!, secret);
            using var keyRequest = await data.GetAsync(ManagementApi.Uri("/v1/projects/" +
                (await restored.Services.GetRequiredService<IApiKeyStore>().FindAsync(secret, token) switch
                {
                    ApiKey key => key.ProjectId!,
                    NotFound => throw new InvalidOperationException("the stale key fixture is missing"),
                }) + "/databases/main/collections"), token);
            await Assert.That(keyRequest.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            var gate = restored.Services.GetRequiredService<ICatalogGate>();
            await Assert.That(gate.IsOpen).IsFalse();
            await Assert.That(gate.Trusted).IsFalse();
            using var channel = PeerChannel.Create(restored.PeerAddress!, PeerTls.Credential);
            var peer = new TerraBase.Features.Replication.V1.Replication.ReplicationClient(channel);
            var digest = await peer.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(ChangeGroups.System, null) }, cancellationToken: token);
            await Assert.That(digest.Held).IsFalse();
            var incarnation = (await restored.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token)).Incarnation;
            var request = new BucketRequest { Database = ChangeGroups.Wire(ChangeGroups.System, incarnation) };
            request.Buckets.Add(0);
            using var pages = peer.ReadBucket(request, cancellationToken: token);
            var refused = await Assert.ThrowsAsync<RpcException>(() => pages.ResponseStream.MoveNext(token));
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
            using var protectedRoute = await restored.Client.GetAsync(ManagementApi.Uri("/v1/management/organizations"), token);
            await Assert.That(protectedRoute.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            using var provision = await restored.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
                new { name = "refused", organizationId = EntityIds.New().ToString(), scopes = ReadScopes }, token);
            await Assert.That(provision.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await restored.Services.GetRequiredService<TerraBase.Features.Backup.IContinuousBackup>().RunOnceAsync(token);
            await Assert.That(restored.Services.GetRequiredService<TerraBase.Features.Backup.IContinuousBackup>().Status.Files).IsEmpty();
            await Assert.That(s3.InconsistentAnchorReads).IsEqualTo(0);
            using var ready = await restored.Client.GetAsync(ManagementApi.Uri("/readyz"), token);
            await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            using var status = await restored.Client.GetAsync(ManagementApi.Uri(CatalogGateStatusEndpoint.Path), token);
            await Assert.That(status.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    private static async IAsyncEnumerable<Stream> SegmentsAsync(Stream segment)
    {
        yield return segment;
        await Task.CompletedTask;
    }

}
