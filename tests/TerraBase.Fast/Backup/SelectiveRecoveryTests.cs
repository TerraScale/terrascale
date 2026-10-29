using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Fast.Management;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Primitives;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class SelectiveRecoveryTests
{
    [Test]
    public async Task Out_of_retention_points_are_refused_even_when_objects_remain(CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        fixture.Time.Advance(TimeSpan.FromDays(2));
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var backups = await fixture.Recovery.GetAsync(ManagementApi.Uri(fixture.Database.Data + "/backups"), token);
            backups.EnsureSuccessStatusCode();
            var listed = await backups.Content.ReadFromJsonAsync<JsonElement>(token);
            return listed.GetProperty("items").EnumerateArray().Any(backup => backup.GetProperty("trigger").GetString() == "scheduled");
        }, TimeSpan.FromSeconds(30));
        using var response = await fixture.Recovery.PostAsJsonAsync(fixture.Route("recovery-preview"), new
        {
            pointInTime = fixture.Point,
            selections = new[] { new { collection = "people" } },
        }, cancellationToken);
        await Assert.That(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound).IsTrue();
        await fixture.AssertCleanAsync();
    }

    [Test]
    [Arguments("recovery-preview")]
    [Arguments("recover")]
    public async Task Database_write_scope_alone_cannot_read_or_restore_documents(string operation, CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var client = fixture.Api.WithToken(fixture.Api.Issuer.Mint(fixture.Api.User, token =>
        {
            token.Claims["org_id"] = fixture.Database.Organization;
            token.Claims["project_id"] = fixture.Database.Project;
            token.Claims["scope"] = "databases:write";
        }));
        using var response = await client.PostAsJsonAsync(fixture.Route(operation), new { }, cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    [Arguments(PlatformDatabases.AuthProjectId, PlatformDatabases.Auth)]
    [Arguments(PlatformDatabases.DashboardProjectId, PlatformDatabases.Dashboard)]
    public async Task Platform_databases_are_always_refused(string project, string database, CancellationToken cancellationToken)
    {
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings[TerraBaseTestServer.PlatformSeedKey] = "true";
            settings["auth-issuer"] = TerraBaseOptions.PlatformAuthIssuer;
            settings["auth-audience"] = TerraBaseOptions.PlatformAuthAudience;
        }, services => services.AddHttpClient("ts-auth").AddHttpMessageHandler(() => new GlobalIssuerRoute()));
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await api.Server.Services.GetRequiredService<IManagementService>().FindDatabaseRecordAsync(PlatformDatabases.OrganizationId, project, database, token) is ManagedDatabase);
        using var client = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Issuer = TerraBaseOptions.PlatformAuthIssuer;
            token.Audience = TerraBaseOptions.PlatformAuthAudience;
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "databases:write documents:read documents:write";
        }));
        foreach (var operation in new[] { "recovery-preview", "recover" })
        {
            using var response = await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{database}/point-in-time/{operation}"), new { }, cancellationToken);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("recovery_erasure_fenced");
        }
    }

    [Test]
    public async Task Free_recovers_only_approved_records_without_another_database(CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var fullRestore = await fixture.Recovery.PostAsJsonAsync(fixture.Route("restore"), new { pointInTime = fixture.Point, name = "quota-refused" }, cancellationToken);
        await Assert.That(fullRestore.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var refusal = await fullRestore.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(refusal.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("database_limit");
        var preview = await fixture.PreviewAsync(cancellationToken);
        await Assert.That(preview.GetProperty("records").GetArrayLength()).IsEqualTo(2);
        await Assert.That(preview.GetProperty("records")[0].GetProperty("current").ValueKind).IsEqualTo(JsonValueKind.Null);
        using var applied = await fixture.ApplyAsync(preview, ["first"], cancellationToken);
        applied.EnsureSuccessStatusCode();
        await Assert.That(await fixture.TextAsync("people", "first", cancellationToken)).IsEqualTo(fixture.Text);
        using var absent = await fixture.Data.GetAsync(fixture.Document("people", "second"), cancellationToken);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await fixture.TextAsync("other", "kept", cancellationToken)).IsEqualTo("newer");
        using var listed = await fixture.Api.Client.GetAsync(ManagementApi.Uri(fixture.Database.ProjectPath + "/databases"), cancellationToken);
        var databases = await listed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(databases.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await fixture.AssertCleanAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Newer_live_or_deleted_versions_refuse_the_entire_approval(bool deleteAgain, CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        var preview = await fixture.PreviewAsync(cancellationToken);
        using (var changed = await fixture.Data.PutAsJsonAsync(fixture.Document("people", "second"), new { text = "newer" }, cancellationToken))
        {
            changed.EnsureSuccessStatusCode();
        }

        if (deleteAgain)
        {
            using var deleted = await fixture.Data.DeleteAsync(fixture.Document("people", "second"), cancellationToken);
            deleted.EnsureSuccessStatusCode();
        }

        using var applied = await fixture.ApplyAsync(preview, ["first", "second"], cancellationToken);
        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        using var first = await fixture.Data.GetAsync(fixture.Document("people", "first"), cancellationToken);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        if (!deleteAgain)
        {
            await Assert.That(await fixture.TextAsync("people", "second", cancellationToken)).IsEqualTo("newer");
        }

        await fixture.AssertCleanAsync();
    }

    [Test]
    public async Task Recovery_preserves_storage_quota_and_does_not_partially_write(CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        var preview = await fixture.PreviewAsync(cancellationToken);
        fixture.StorageLimit = 1;
        using var applied = await fixture.ApplyAsync(preview, ["first", "second"], cancellationToken);
        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        var body = await applied.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("storage_quota");
        using var absent = await fixture.Data.GetAsync(fixture.Document("people", "first"), cancellationToken);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await fixture.AssertCleanAsync();
    }

    [Test]
    public async Task Disk_headroom_refuses_recovery_and_success_releases_its_scratch_lease(CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        var bytes = new DriveInfo(fixture.Api.Server.DataDirectory).AvailableFreeSpace - DiskBudget.Headroom - (8L << 20);
        using (fixture.Api.Server.Services.GetRequiredService<IDiskBudget>().Reserve(bytes))
        {
            using var refused = await fixture.Recovery.PostAsJsonAsync(fixture.Route("recovery-preview"), new
            {
                pointInTime = fixture.Point,
                selections = new[] { new { collection = "people" } },
            }, cancellationToken);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("disk_capacity");
            await Assert.That(refused.Headers.RetryAfter).IsNotNull();
            await Assert.That(fixture.ScratchFiles()).IsEmpty();
        }
        _ = await fixture.PreviewAsync(cancellationToken);
        await fixture.AssertCleanAsync();
    }

    [Test]
    public async Task Revoked_project_access_invalidates_an_existing_approval(CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        var preview = await fixture.PreviewAsync(cancellationToken);
        using var closed = await fixture.Api.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{fixture.Database.Project}/closure"),
            new { version = 2, closureId = EntityIds.New().ToString(), authorityOrganizationId = fixture.Database.Organization }, cancellationToken);
        closed.EnsureSuccessStatusCode();
        using var applied = await fixture.ApplyAsync(preview, ["first"], cancellationToken);
        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await fixture.AssertCleanAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Modified_or_expired_approvals_are_refused(bool expire, CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        var preview = await fixture.PreviewAsync(cancellationToken);
        var approval = preview.GetProperty("approval").GetString()!;
        if (expire)
        {
            fixture.Time.Advance(TimeSpan.FromMinutes(11));
        }
        else
        {
            approval = (approval[0] == 'A' ? "B" : "A") + approval[1..];
        }

        using var response = await fixture.Recovery.PostAsJsonAsync(fixture.Route("recover"), new
        {
            approval,
            records = new[] { new { collection = "people", id = ManagementApi.DocumentId("first") } },
        }, cancellationToken);
        await Assert.That(response.IsSuccessStatusCode).IsFalse();
        using var absent = await fixture.Data.GetAsync(fixture.Document("people", "first"), cancellationToken);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await fixture.AssertCleanAsync();
    }

    [Test]
    public async Task Cancelling_a_preview_download_removes_private_recovery_files(CancellationToken cancellationToken)
    {
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.S3.BeforeGet = async (key, token) =>
        {
            if (!key.Contains("/snapshots/", StringComparison.Ordinal))
            {
                return;
            }

            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                released.TrySetResult();
            }
        };
        var request = fixture.PreviewAsync(cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        using var competing = await fixture.Recovery.PostAsJsonAsync(fixture.Route("recovery-preview"), new { }, cancellationToken);
        await Assert.That(competing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(competing.Headers.RetryAfter).IsNotNull();
        cancel.Cancel();
        await Assert.That(async () => await request).Throws<OperationCanceledException>();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(fixture.ScratchFiles().Length == 0));
    }

    private sealed class GlobalIssuerRoute : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(request.RequestUri!.AbsoluteUri.Replace(TerraBaseOptions.PlatformAuthIssuer, TestIssuer.Issuer, StringComparison.Ordinal));
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri.AbsoluteUri == TestIssuer.Discovery && response.IsSuccessStatusCode)
            {
                var discovery = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Content.Dispose();
                response.Content = new StringContent(discovery.Replace(TestIssuer.Issuer, TerraBaseOptions.PlatformAuthIssuer, StringComparison.Ordinal), Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ManagementApi Api { get; private set; } = null!;
        public HttpClient Data { get; private set; } = null!;
        public HttpClient Recovery { get; private set; } = null!;
        public ManagedDatabaseRef Database { get; private set; } = null!;
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        public FakeS3 S3 { get; } = new();
        public string Text { get; } = new Faker().Lorem.Sentence();
        public long StorageLimit { get; set; } = long.MaxValue;
        public DateTimeOffset Point { get; private set; }

        public static async Task<Fixture> StartAsync(CancellationToken cancellationToken)
        {
            var fixture = new Fixture();
            fixture.Api = await ManagementApi.StartAsync(fixture.S3.Configure, services =>
            {
                fixture.S3.Route(services);
                ManualBackupHosting.Configure(services);
                services.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance()));
                new UsageCommitHook(storageLimit: _ => fixture.StorageLimit).InstallQuota(services);
            }, fixture.Time);
            try
            {
                fixture.Database = await fixture.Api.CreateManagedDatabaseAsync();
                fixture.Recovery = fixture.Api.WithToken(fixture.Api.Issuer.Mint(fixture.Api.User, token =>
                {
                    token.Claims["project_id"] = fixture.Database.Project;
                    token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
                }));
                fixture.Data = await fixture.Api.CreateClientAsync(fixture.Database.Organization, "documents:read", "documents:write");
                foreach (var collection in new[] { "people", "other" })
                {
                    await ManagementApi.CreateCollectionAsync(fixture.Data, fixture.Database, collection, cancellationToken);
                }

                foreach (var (collection, id) in new[] { ("people", "first"), ("people", "second"), ("other", "kept") })
                {
                    using var written = await fixture.Data.PutAsJsonAsync(fixture.Document(collection, id), new { text = fixture.Text }, cancellationToken);
                    written.EnsureSuccessStatusCode();
                }

                fixture.Time.Advance(TimeSpan.FromSeconds(10));
                await fixture.Api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
                fixture.Point = fixture.Time.GetUtcNow();
                foreach (var id in new[] { "first", "second" })
                {
                    using var deleted = await fixture.Data.DeleteAsync(fixture.Document("people", id), cancellationToken);
                    deleted.EnsureSuccessStatusCode();
                }

                using var changed = await fixture.Data.PutAsJsonAsync(fixture.Document("other", "kept"), new { text = "newer" }, cancellationToken);
                changed.EnsureSuccessStatusCode();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public Uri Document(string collection, string id) => ManagementApi.Uri(ManagementApi.DocumentPath(Database, collection, id));
        public Uri Route(string operation) => ManagementApi.Uri(Database.Data + "/point-in-time/" + operation);
        public async Task<JsonElement> PreviewAsync(CancellationToken cancellationToken, HttpClient? client = null)
        {
            using var response = await (client ?? Recovery).PostAsJsonAsync(Route("recovery-preview"), new
            {
                pointInTime = Point,
                selections = new[] { new { collection = "people" } },
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }

        public Task<HttpResponseMessage> ApplyAsync(JsonElement preview, string[] ids, CancellationToken cancellationToken, HttpClient? client = null) =>
            (client ?? Recovery).PostAsJsonAsync(Route("recover"), new
            {
                approval = preview.GetProperty("approval").GetString(),
                records = ids.Select(id => new { collection = "people", id = ManagementApi.DocumentId(id) }).ToArray(),
            }, cancellationToken);

        public async Task<string?> TextAsync(string collection, string id, CancellationToken cancellationToken)
        {
            using var response = await Data.GetAsync(Document(collection, id), cancellationToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString();
        }

        public string[] ScratchFiles() => Directory.GetFiles(Path.Combine(Api.Server.DataDirectory, ".backup"), "*.db*");
        public async Task AssertCleanAsync()
        {
            var meter = Api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
            long reserved = -1;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, observer) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "terrabase.disk.reserved")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => reserved = value);
            listener.Start();
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                listener.RecordObservableInstruments();
                return Task.FromResult(reserved == 0 && ScratchFiles().Length == 0);
            }, TimeSpan.FromSeconds(30));
            await Assert.That(ScratchFiles()).IsEmpty();
            await Assert.That(reserved).IsEqualTo(0);
        }
        public async ValueTask DisposeAsync()
        {
            Data?.Dispose();
            Recovery?.Dispose();
            await Api.DisposeAsync();
        }
    }
}
