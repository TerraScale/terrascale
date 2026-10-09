using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Fast.Management;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupSecretLifecycleTests
{
    [Test]
    [Arguments(HttpStatusCode.BadRequest)]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Forbidden)]
    public async Task Repeated_definitive_create_rejections_preserve_backlog_capacity_for_replacement_and_detach(HttpStatusCode status)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        await SaveAsync(api, route, "customer", token);
        var predecessor = oci.Secrets.Keys.Single();
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        var seeded = BackupSecretLifecycle.MaxPending - 1;
        for (var offset = 0; offset < seeded; offset += 128)
        {
            var count = Math.Min(128, seeded - offset);
            await catalog.WriteDurableAsync(async transaction =>
            {
                for (var index = 0; index < count; index++)
                {
                    var id = Ulid.NewUlid().ToString();
                    var work = new BackupSecretWork(1, id, "terrabase-backup-" + id, null, true, time.GetUtcNow().AddDays(1));
                    await SystemCatalog.PutAsync(transaction, BackupSecretLifecycle.Collection, id, work, TerraBaseJsonContext.Default.BackupSecretWork, token);
                }

                return true;
            }, token);
        }

        var rejections = 0;
        oci.SecretResponseOverride = (method, path) =>
        {
            if (method != HttpMethod.Post || path != "/20180608/secrets")
            {
                return null;
            }

            Interlocked.Increment(ref rejections);
            return new(status) { Content = new StringContent(FakeOci.CustomerSecretKey) };
        };
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, Destination("both"), token), HttpStatusCode.ServiceUnavailable,
                "unavailable", "customer backup bucket or OCI Vault could not be accessed");
            await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(seeded);
        }

        await Assert.That(Volatile.Read(ref rejections)).IsEqualTo(8);
        await Assert.That(oci.Secrets.Keys).IsEquivalentTo([predecessor]);
        await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
        using var unchanged = await api.Client.GetAsync(route, token);
        unchanged.EnsureSuccessStatusCode();
        await Assert.That((await unchanged.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("mode").GetString()).IsEqualTo("customer");
        oci.SecretResponseOverride = null;
        await SaveAsync(api, route, "both", token);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([predecessor]);
        await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(seeded);
        using var detached = await api.Client.DeleteAsync(route, token);
        await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var absent = await api.Client.GetAsync(route, token);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo(oci.Secrets.Keys);
        await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(seeded);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Definitive_rejection_removes_its_intent_while_all_remote_cleanup_slots_are_stalled()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var organization = await api.CreateOrganizationAsync(new Faker().Company.CompanyName());
        var routes = new List<Uri>(4);
        for (var index = 0; index < 4; index++)
        {
            var project = await api.CreateProjectAsync(organization, "Retirement " + index);
            var route = Uri($"/v1/management/organizations/{organization}/projects/{project}/backup-destination");
            await SaveAsync(api, route, "customer", token);
            routes.Add(route);
        }

        var predecessors = oci.Secrets.Keys.ToArray();
        var replacement = await api.CreateProjectAsync(organization, "Definitive rejection");
        var replacing = Uri($"/v1/management/organizations/{organization}/projects/{replacement}/backup-destination");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = 0;
        oci.BeforeSecretRequest = async (_, path, cancellationToken) =>
        {
            if (path.EndsWith("/actions/scheduleDeletion", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref retiring) == 4)
                {
                    entered.TrySetResult();
                }

                await release.Task.WaitAsync(cancellationToken);
            }
        };
        using var deleting = CancellationTokenSource.CreateLinkedTokenSource(token);
        var deletions = routes.Select(route => api.Client.DeleteAsync(route, deleting.Token)).ToArray();
        try
        {
            await entered.Task.WaitAsync(token);
            await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(4);
            oci.SecretResponseOverride = (method, path) => method == HttpMethod.Post && path == "/20180608/secrets"
                ? new(HttpStatusCode.Forbidden) : null;
            await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(replacing, Destination("customer"), token), HttpStatusCode.ServiceUnavailable,
                "unavailable", "customer backup bucket or OCI Vault could not be accessed");
            var pending = await PendingAsync(api, token);
            await Assert.That(pending.Count).IsEqualTo(4);
            await Assert.That(pending.Select(work => work.SecretId!)).IsEquivalentTo(predecessors);
            await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
            oci.SecretResponseOverride = null;
            await SaveAsync(api, replacing, "customer", token);
            await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(4);
            release.TrySetResult();
            foreach (var deletion in deletions)
            {
                using var response = await deletion.WaitAsync(token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(0);
            await Assert.That(oci.ScheduledSecrets).IsEquivalentTo(predecessors);
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
            await deleting.CancelAsync();
            await ((Task)Task.WhenAll(deletions)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var deletion in deletions.Where(task => task.IsCompletedSuccessfully))
            {
                using var response = await deletion;
            }
        }
    }

    [Test]
    public async Task Definitive_rejection_retries_transient_catalog_cleanup_failures_before_releasing_save_admission()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services =>
        {
            Manual(oci, services);
            hook.Install(services);
        }, time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        var rejections = 0;
        var cleanups = 0;
        oci.SecretResponseOverride = (method, path) =>
        {
            if (method != HttpMethod.Post || path != "/20180608/secrets")
            {
                return null;
            }

            Interlocked.Increment(ref rejections);
            return new(HttpStatusCode.BadRequest);
        };
        hook.AfterGet = (collection, _, _) =>
        {
            if (collection == BackupSecretLifecycle.Collection && Volatile.Read(ref rejections) != 0
                && Interlocked.Increment(ref cleanups) <= 2)
            {
                throw new StorageBusyException();
            }

            return ValueTask.CompletedTask;
        };
        await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, Destination("customer"), token), HttpStatusCode.ServiceUnavailable,
            "unavailable", "customer backup bucket or OCI Vault could not be accessed");
        await Assert.That(Volatile.Read(ref rejections)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref cleanups)).IsEqualTo(3);
        await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
        hook.AfterGet = null;
        oci.SecretResponseOverride = null;
        await SaveAsync(api, route, "customer", token);
        await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(1);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Vault_admission_rejection_discards_the_unsubmitted_intent_and_allows_recovery()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        await SaveAsync(api, route, "customer", token);
        var current = oci.Secrets.Keys.Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;
        var submitted = 0;
        oci.BeforeSecretRequest = async (method, path, cancellationToken) =>
        {
            if (method == HttpMethod.Post && path == "/20180608/secrets")
            {
                Interlocked.Increment(ref submitted);
            }

            if (method == HttpMethod.Get && path == "/20190301/secretbundles/" + current)
            {
                if (Interlocked.Increment(ref admitted) == 8)
                {
                    entered.TrySetResult();
                }

                await release.Task.WaitAsync(cancellationToken);
            }
        };
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
        var secrets = api.Server.Services.GetRequiredService<IOciVaultSecrets>();
        var reads = Enumerable.Range(0, 8).Select(_ => secrets.ReadAsync(current, reading.Token)).ToArray();
        try
        {
            await entered.Task.WaitAsync(token);
            await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, Destination("both"), token), HttpStatusCode.ServiceUnavailable,
                "unavailable", "customer backup bucket or OCI Vault could not be accessed");
            await Assert.That(Volatile.Read(ref submitted)).IsEqualTo(0);
            await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(0);
            await Assert.That(oci.Secrets.Keys).IsEquivalentTo([current]);
            await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
            release.TrySetResult();
            await Task.WhenAll(reads).WaitAsync(token);
            oci.BeforeSecretRequest = null;
            await SaveAsync(api, route, "both", token);
            await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([current]);
            await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(0);
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
            await reading.CancelAsync();
            await ((Task)Task.WhenAll(reads)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Cancellation_discards_only_a_confirmed_unsubmitted_create(bool beforeSubmission)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services =>
        {
            Manual(oci, services);
            services.AddSingleton<IStartupFilter>(new ObserveAbort(aborted));
            if (beforeSubmission)
            {
                services.AddSingleton<OciVaultSecrets>();
                services.AddSingleton<IOciVaultSecrets>(provider => new GateCreate(
                    provider.GetRequiredService<OciVaultSecrets>(), entered, release, token));
            }
        }, time);
        var database = await api.CreateManagedDatabaseAsync();
        var submitted = 0;
        oci.BeforeSecretRequest = async (method, path, cancellationToken) =>
        {
            if (method == HttpMethod.Post && path == "/20180608/secrets")
            {
                Interlocked.Increment(ref submitted);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var route = Uri(database.ProjectPath + "/backup-destination");
        var saving = api.Client.PutAsJsonAsync(route, Destination("customer"), caller.Token);
        try
        {
            await entered.Task.WaitAsync(token);
            await Assert.That((await PendingAsync(api, token)).Single().Retiring).IsFalse();
            await caller.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => saving);
            await aborted.Task.WaitAsync(token);
            release.TrySetResult();
            await EventuallyAsync(async () => beforeSubmission
                ? (await PendingAsync(api, token)).Count == 0
                : (await PendingAsync(api, token)).Single().Retiring, token);
            await Assert.That(Volatile.Read(ref submitted)).IsEqualTo(beforeSubmission ? 0 : 1);
            await Assert.That(oci.Secrets.Count).IsEqualTo(0);
            using var absent = await api.Client.GetAsync(route, token);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            oci.BeforeSecretRequest = null;
            await SaveAsync(api, route, "customer", token);
            await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(beforeSubmission ? 0 : 1);
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
            await caller.CancelAsync();
            await ((Task)saving).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (saving.IsCompletedSuccessfully)
            {
                using var response = await saving;
            }
        }
    }

    [Test]
    public async Task Failed_replacement_and_detach_retirements_survive_project_deletion_and_a_server_restart()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        var directory = Directory.CreateTempSubdirectory("terrabase-secret-retirement-").FullName;
        ManagementApi? first = null;
        ManagementApi? restarted = null;
        try
        {
            first = await ManagementApi.StartAsync(settings => Configure(settings), services => Manual(oci, services), time);
            var organization = await first.CreateOrganizationAsync(new Faker().Company.CompanyName());
            var project = await first.CreateProjectAsync(organization, "Retirement");
            var projectRoute = $"/v1/management/organizations/{organization}/projects/{project}";
            var route = Uri(projectRoute + "/backup-destination");
            await SaveAsync(first, route, "both", token);
            var predecessor = oci.Secrets.Keys.Single();
            oci.SecretResponseOverride = (_, path) => path.EndsWith("/actions/scheduleDeletion", StringComparison.Ordinal)
                ? new(HttpStatusCode.ServiceUnavailable) : null;
            await SaveAsync(first, route, "customer", token);
            var current = oci.Secrets.Keys.Single(id => id != predecessor);
            var pending = await PendingAsync(first, token);
            await Assert.That(pending.Select(work => work.SecretId!)).IsEquivalentTo([predecessor]);
            using var detached = await first.Client.DeleteAsync(route, token);
            await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            using var deleted = await first.Client.DeleteAsync(Uri(projectRoute), token);
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            pending = await PendingAsync(first, token);
            await Assert.That(pending.Select(work => work.SecretId!)).IsEquivalentTo([predecessor, current]);
            await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
            await AssertMetadataOnlyAsync(first, token);
            await first.DisposeAsync();
            await first.Server.CleanupCompletion;
            first = null;

            oci.SecretResponseOverride = null;
            restarted = await ManagementApi.StartAsync(settings => Configure(settings), services => Manual(oci, services), time);
            await Assert.That((await PendingAsync(restarted, token)).Count).IsEqualTo(2);
            time.Advance(TimeSpan.FromMinutes(1));
            await EventuallyAsync(async () => (await PendingAsync(restarted, token)).Count == 0, token);
            await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([predecessor, current]);
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            if (first is not null)
            {
                try { await first.DisposeAsync(); }
                finally { await first.Server.CleanupCompletion; }
            }

            if (restarted is not null)
            {
                try { await restarted.DisposeAsync(); }
                finally { await restarted.Server.CleanupCompletion; }
            }

            Directory.Delete(directory, recursive: true);
        }

        void Configure(Dictionary<string, string> settings)
        {
            oci.ConfigureCustomer(settings);
            settings["data-directory"] = directory;
        }
    }

    [Test]
    [Arguments("malformed")]
    [Arguments("transport")]
    [Arguments("http-timeout")]
    [Arguments("http-unavailable")]
    [Arguments("foreign-metadata")]
    [Arguments("duplicate-metadata")]
    [Arguments("null-metadata")]
    public async Task A_committed_create_with_an_unreadable_id_is_reconciled_by_its_persisted_name(string failure)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        await SaveAsync(api, route, "customer", token);
        var current = oci.Secrets.Keys.Single();
        oci.CreatedSecretResponseOverride = (_, _) => new(failure switch
        {
            "http-timeout" => HttpStatusCode.RequestTimeout,
            "http-unavailable" => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.OK,
        })
        {
            Content = failure == "transport" ? new StreamContent(new FailingBody()) : new StringContent("invalid-json"),
        };
        using var failed = await api.Client.PutAsJsonAsync(route, Destination("both"), token);
        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var pending = (await PendingAsync(api, token)).Single();
        await Assert.That(pending.SecretId).IsNull();
        await Assert.That(pending.Retiring).IsTrue();
        await Assert.That(Ulid.TryParse(pending.Id, out _)).IsTrue();
        await Assert.That(pending.SecretName).IsEqualTo("terrabase-backup-" + pending.Id);
        var orphan = oci.SecretNames[pending.SecretName!];
        await AssertMetadataOnlyAsync(api, token);

        // A not-yet-visible create must survive a complete empty response, even after its foreground request ended.
        var emptyConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oci.SecretResponseOverride = (method, path) => method == HttpMethod.Get && path == "/20180608/secrets"
            ? new(HttpStatusCode.OK) { Content = new ConsumedContent("[]", emptyConsumed) } : null;
        time.Advance(TimeSpan.FromMinutes(1));
        await emptyConsumed.Task.WaitAsync(token);
        await EventuallyAsync(async () => (await PendingAsync(api, token)).Single().DueAt > time.GetUtcNow(), token);
        await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
        oci.SecretResponseOverride = null;
        if (failure.EndsWith("-metadata", StringComparison.Ordinal))
        {
            var metadata = JsonSerializer.Serialize(new
            {
                id = orphan,
                secretName = pending.SecretName,
                vaultId = failure == "foreign-metadata" ? "ocid1.vault.oc1.foreign" : "ocid1.vault.oc1.test.backup",
                compartmentId = "ocid1.compartment.oc1..backup",
                lifecycleState = "ACTIVE",
            });
            var listing = failure switch { "duplicate-metadata" => "[" + metadata + "," + metadata + "]", "null-metadata" => "[null]", _ => "[" + metadata + "]" };
            var rejectedConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            oci.SecretResponseOverride = (method, path) => method == HttpMethod.Get && path == "/20180608/secrets"
                ? new(HttpStatusCode.OK) { Content = new ConsumedContent(listing, rejectedConsumed) } : null;
            time.Advance(TimeSpan.FromMinutes(1));
            await rejectedConsumed.Task.WaitAsync(token);
            await EventuallyAsync(async () => (await PendingAsync(api, token)).Single().DueAt > time.GetUtcNow(), token);
            await Assert.That((await PendingAsync(api, token)).Single().SecretId).IsNull();
            await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
            oci.SecretResponseOverride = null;
        }

        time.Advance(TimeSpan.FromMinutes(1));
        await EventuallyAsync(async () => (await PendingAsync(api, token)).Count == 0, token);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([orphan]);
        using var unchanged = await api.Client.GetAsync(route, token);
        unchanged.EnsureSuccessStatusCode();
        await Assert.That((await unchanged.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("mode").GetString()).IsEqualTo("customer");
        await Assert.That(oci.ScheduledSecrets.Contains(current, StringComparer.Ordinal)).IsFalse();
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Cancellation_after_successful_create_headers_still_reads_the_id_and_retires_it_without_a_timer_tick()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services =>
        {
            Manual(oci, services);
            services.AddSingleton<IStartupFilter>(new ObserveAbort(aborted));
        }, time);
        var database = await api.CreateManagedDatabaseAsync();
        GatedBody? body = null;
        oci.CreatedSecretResponseOverride = (_, id) => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(body = new GatedBody(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id })))),
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var saving = api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination("customer"), caller.Token);
        try
        {
            await EventuallyAsync(() => Task.FromResult(body is not null), token);
            await body!.Entered.Task.WaitAsync(token);
            // The intent exists before any successful response body can hand its ID to the application.
            await Assert.That((await PendingAsync(api, token)).Single().Retiring).IsFalse();
            await caller.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => saving);
            await aborted.Task.WaitAsync(token);
            await Assert.That(body.ReadToken.IsCancellationRequested).IsFalse();
            body.Release();
            await EventuallyAsync(async () => oci.ScheduledSecrets.Count == 1 && (await PendingAsync(api, token)).Count == 0, token);
            await Assert.That(body.Completed).IsTrue();
            using var absent = await api.Client.GetAsync(Uri(database.ProjectPath + "/backup-destination"), token);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(oci.ScheduledSecrets).IsEquivalentTo(oci.Secrets.Keys);
        }
        finally
        {
            body?.Release();
            await caller.CancelAsync();
            await ((Task)saving).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (saving.IsCompletedSuccessfully)
            {
                using var response = await saving;
            }
        }
    }

    [Test]
    public async Task A_worker_claim_prevents_a_delayed_create_from_publishing_a_secret_scheduled_for_deletion()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        GatedBody? body = null;
        oci.CreatedSecretResponseOverride = (_, id) => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(body = new GatedBody(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id })))),
        };
        var route = Uri(database.ProjectPath + "/backup-destination");
        var saving = api.Client.PutAsJsonAsync(route, Destination("customer"), token);
        try
        {
            await EventuallyAsync(() => Task.FromResult(body is not null), token);
            await body!.Entered.Task.WaitAsync(token);
            time.Advance(TimeSpan.FromMinutes(2));
            await EventuallyAsync(async () => oci.ScheduledSecrets.Count == 1 && (await PendingAsync(api, token)).Count == 0, token);
            body.Release();
            using var rejected = await saving.WaitAsync(token);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            using var absent = await api.Client.GetAsync(route, token);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(oci.ScheduledSecrets).IsEquivalentTo(oci.Secrets.Keys);
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            body?.Release();
            await ((Task)saving).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task A_full_durable_backlog_rejects_save_and_detach_before_losing_the_current_secret()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        await SaveAsync(api, route, "customer", token);
        var current = oci.Secrets.Keys.Single();
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        for (var offset = 0; offset < BackupSecretLifecycle.MaxPending; offset += 128)
        {
            await catalog.WriteAsync(async transaction =>
            {
                for (var index = 0; index < 128; index++)
                {
                    var id = Ulid.NewUlid().ToString();
                    var work = new BackupSecretWork(1, id, "terrabase-backup-" + id, null, true, time.GetUtcNow().AddDays(1));
                    await SystemCatalog.PutAsync(transaction, BackupSecretLifecycle.Collection, id, work, TerraBaseJsonContext.Default.BackupSecretWork, token);
                }

                return true;
            }, token);
        }

        using var rejectedSave = await api.Client.PutAsJsonAsync(route, Destination("both"), token);
        using var rejectedDetach = await api.Client.DeleteAsync(route, token);
        await Assert.That(rejectedSave.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(rejectedDetach.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var configured = await api.Client.GetAsync(route, token);
        configured.EnsureSuccessStatusCode();
        await Assert.That((await configured.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("mode").GetString()).IsEqualTo("customer");
        await Assert.That(oci.Secrets.Keys).IsEquivalentTo([current]);
        await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
        await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(BackupSecretLifecycle.MaxPending);
    }

    [Test]
    public async Task Known_id_not_found_remains_pending_and_a_scheduled_secret_retry_preserves_its_original_deletion_date()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        await SaveAsync(api, route, "customer", token);
        var id = oci.Secrets.Keys.Single();
        var missing = 0;
        oci.SecretResponseOverride = (method, path) =>
        {
            if (method != HttpMethod.Get || path != "/20180608/secrets/" + id)
            {
                return null;
            }

            Interlocked.Increment(ref missing);
            return new(HttpStatusCode.NotFound);
        };
        using var detached = await api.Client.DeleteAsync(route, token);
        await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        time.Advance(TimeSpan.FromMinutes(1));
        await EventuallyAsync(() => Task.FromResult(Volatile.Read(ref missing) == 2), token);
        var work = (await PendingAsync(api, token)).Single();
        await Assert.That(work.SecretId).IsEqualTo(id);
        await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
        oci.SecretResponseOverride = null;
        time.Advance(TimeSpan.FromMinutes(1));
        await EventuallyAsync(async () => (await PendingAsync(api, token)).Count == 0, token);
        var deletion = oci.SecretDeletionTimes[id];

        // Simulate a lost local acknowledgement after OCI committed scheduleDeletion.
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        await catalog.WriteAsync(async transaction =>
        {
            await SystemCatalog.PutAsync(transaction, BackupSecretLifecycle.Collection, work.Id, work with { DueAt = time.GetUtcNow() },
                TerraBaseJsonContext.Default.BackupSecretWork, token);
            return true;
        }, token);
        time.Advance(TimeSpan.FromMinutes(1));
        await EventuallyAsync(async () => (await PendingAsync(api, token)).Count == 0, token);
        await Assert.That(oci.SecretDeletionTimes[id]).IsEqualTo(deletion);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Unmatched_names_in_the_first_page_do_not_starve_a_later_known_retirement()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        await SaveAsync(api, route, "customer", token);
        var secretId = oci.Secrets.Keys.Single();
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        await catalog.WriteAsync(async transaction =>
        {
            for (var index = 0; index < 9; index++)
            {
                var id = new string('0', 25) + (char)('0' + index);
                var work = new BackupSecretWork(1, id, "terrabase-backup-" + id, null, true, time.GetUtcNow());
                await SystemCatalog.PutAsync(transaction, BackupSecretLifecycle.Collection, id, work, TerraBaseJsonContext.Default.BackupSecretWork, token);
            }

            return true;
        }, token);
        oci.SecretResponseOverride = (_, path) => path.EndsWith("/actions/scheduleDeletion", StringComparison.Ordinal)
            ? new(HttpStatusCode.ServiceUnavailable) : null;
        using var detached = await api.Client.DeleteAsync(route, token);
        await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        oci.SecretResponseOverride = null;
        time.Advance(TimeSpan.FromMinutes(1));
        await EventuallyAsync(async () => (await PendingAsync(api, token)).Count(work => work.SecretId is null && work.DueAt > time.GetUtcNow()) == 8, token);
        await Assert.That(oci.ScheduledSecrets.Count).IsEqualTo(0);
        time.Advance(TimeSpan.FromMinutes(1));
        await EventuallyAsync(async () => (await PendingAsync(api, token)).Count == 9, token);
        await Assert.That(oci.ScheduledSecrets).IsEquivalentTo([secretId]);
        await Assert.That((await PendingAsync(api, token)).All(work => work.SecretId is null)).IsTrue();
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Shutdown_cancels_and_joins_a_foreground_retirement_before_dependencies_are_disposed()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var oci = new FakeOci();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        await SaveAsync(api, route, "customer", token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oci.BeforeSecretRequest = async (_, path, cancellationToken) =>
        {
            if (!path.EndsWith("/actions/scheduleDeletion", StringComparison.Ordinal))
            {
                return;
            }

            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled.TrySetResult();
                throw;
            }
        };
        var deleting = api.Client.DeleteAsync(route, token);
        try
        {
            await entered.Task.WaitAsync(token);
            var lifecycle = api.Server.Services.GetRequiredService<BackupSecretLifecycle>();
            await lifecycle.StopAsync(token).WaitAsync(token);
            await canceled.Task.WaitAsync(token);
            using var detached = await deleting.WaitAsync(token);
            await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That((await PendingAsync(api, token)).Count).IsEqualTo(1);
            using var stillDetached = await api.Client.DeleteAsync(route, token);
            await Assert.That(stillDetached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
        finally
        {
            oci.BeforeSecretRequest = null;
            await ((Task)deleting).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task SaveAsync(ManagementApi api, Uri route, string mode, CancellationToken token)
    {
        using var response = await api.Client.PutAsJsonAsync(route, Destination(mode), token);
        response.EnsureSuccessStatusCode();
    }

    private static object Destination(string mode) => new
    {
        endpoint = FakeOci.CustomerEndpoint,
        region = "test-region-1",
        bucket = FakeOci.CustomerBucket,
        accessKeyId = FakeOci.CustomerAccessKey,
        secretKey = FakeOci.CustomerSecretKey,
        mode,
    };

    private static async Task<IReadOnlyList<BackupSecretWork>> PendingAsync(ManagementApi api, CancellationToken token) =>
        await api.Server.Services.GetRequiredService<SystemCatalog>().ReadAsync(async transaction =>
        {
            var page = await transaction.ListAsync(BackupSecretLifecycle.Collection, null, BackupSecretLifecycle.MaxPending,
                BackupSecretLifecycle.MaxPending * BackupSecretLifecycle.MaxRecordBytes, token);
            return (IReadOnlyList<BackupSecretWork>)[.. page.Documents.Select(document => JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.BackupSecretWork)!)];
        }, token);

    private static async Task AssertMetadataOnlyAsync(ManagementApi api, CancellationToken token)
    {
        var json = string.Join('\n', (await PendingAsync(api, token)).Select(work => JsonSerializer.Serialize(work, TerraBaseJsonContext.Default.BackupSecretWork)));
        await Assert.That(json.Contains(FakeOci.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains(FakeOci.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        }
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private static void Manual(FakeOci oci, IServiceCollection services)
    {
        oci.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private sealed class ObserveAbort(TaskCompletionSource aborted) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                using var registration = context.Request.Method == "PUT" && context.Request.Path.Value!.EndsWith("/backup-destination", StringComparison.Ordinal)
                    ? context.RequestAborted.Register(() => aborted.TrySetResult()) : default;
                await continuation();
            });
            next(app);
        };
    }

    private sealed class GateCreate(IOciVaultSecrets inner, TaskCompletionSource entered, TaskCompletionSource release, CancellationToken token) : IOciVaultSecrets
    {
        public async Task<string> CreateAsync(string name, string accessKeyId, string secretKey, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return await inner.CreateAsync(name, accessKeyId, secretKey, cancellationToken);
        }

        public Task<(string AccessKeyId, string SecretKey)> ReadAsync(string secretId, CancellationToken cancellationToken) => inner.ReadAsync(secretId, cancellationToken);

        public Task<string?> FindAsync(string name, CancellationToken cancellationToken) => inner.FindAsync(name, cancellationToken);

        public Task DeleteAsync(string secretId, CancellationToken cancellationToken) => inner.DeleteAsync(secretId, cancellationToken);
    }

    private sealed class GatedBody(byte[] bytes) : MemoryStream(bytes)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }
        public bool Completed { get; private set; }
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            var read = await base.ReadAsync(buffer, cancellationToken);
            Completed = true;
            return read;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ConsumedContent(string json, TaskCompletionSource consumed) : StringContent(json, Encoding.UTF8, "application/json")
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                consumed.TrySetResult();
            }
        }
    }

    private sealed class FailingBody : MemoryStream
    {
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("create response body could not be read"));
    }
}
