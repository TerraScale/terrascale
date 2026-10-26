using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed partial class DatabaseRecoveryTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Canceled_disposal_or_startup_retains_recovery_files_until_native_cleanup_finishes(bool duringStartup)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        var state = new RecoveryData(execution.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ManagementApi>? startup = null;
        ManagementApi? api = null;
        IStorageTransaction? transaction = null;
        try
        {
            if (duringStartup)
            {
                startup = state.StartAsync(services => services.AddSingleton<IHostedService>(_ => new HeldRecoveryStartup(entered, release, token)));
                await entered.Task.WaitAsync(token);
                await execution.CancelAsync();
                await Assert.That(async () => await startup.WaitAsync(token)).ThrowsException();
            }
            else
            {
                api = await state.StartAsync();
                using var health = await api.Server.Client.GetAsync(Uri("/healthz"), token);
                await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
                transaction = await store.BeginTransactionAsync(write: false, token);
                await execution.CancelAsync();
                await Assert.That(async () => await api.DisposeAsync()).Throws<OperationCanceledException>();
            }
            await Assert.That(async () => await state.DisposeAsync()).Throws<OperationCanceledException>();
            await Assert.That(System.IO.Directory.Exists(state.Directory)).IsTrue();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsTrue();

            release.TrySetResult();
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
                transaction = null;
            }
            await state.CleanupCompletion.WaitAsync(token);
            await Assert.That(System.IO.Directory.Exists(state.Directory)).IsFalse();
        }
        finally
        {
            await execution.CancelAsync();
            release.TrySetResult();
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            if (startup is not null)
            {
                try
                {
                    var started = await startup;
                    started.Client.Dispose();
                }
                catch (Exception) when (startup.IsFaulted || startup.IsCanceled)
                {
                    // Startup cancellation is asserted above; join it before enumerating its cleanup owner.
                }
            }

            api?.Client.Dispose();
            await state.CleanupCompletion.WaitAsync(token);
        }
    }

    [Test]
    [Arguments("delete.intent")]
    [Arguments("delete.main_file")]
    [Arguments("cleanup.files")]
    [Arguments("cleanup.clearing")]
    [Arguments("delete.finalized")]
    public async Task Interrupted_deletion_is_completed_on_restart(string interruption)
    {
        await using var state = new RecoveryData();
        var armed = false;
        var reached = false;
        string target;
        string? replacement = null;
        // Preserve the injected crash state until its assertions; restart still uses the real clock.
        await using (var api = await state.StartAsync(services => services
            .AddSingleton<TimeProvider>(new FakeTimeProvider(TimeProvider.System.GetUtcNow()))
            .AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            if (armed && step == interruption)
            {
                reached = true;
                if (step == "delete.main_file")
                {
                    // The engine already closed the store and removed .db; leave every sidecar behind.
                    foreach (var suffix in state.Sidecars)
                    {
                        File.WriteAllBytes(state.TargetPath + suffix, state.Faker.Random.Bytes(3));
                    }
                }

                throw new InvalidOperationException("interrupted at " + step);
            }
        })))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            target = await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner);
            state.TargetPath = state.Path(target);
            await state.WriteArtifactsAsync(target, sidecars: false);
            armed = true;
            using var deleted = await owner.DeleteAsync(Uri(state.Databases + "/" + target));
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(reached).IsTrue();
            await state.AssertUnregisteredAsync(api, owner, target);
            await Assert.That((await PendingAsync(api)).Documents.Count).IsEqualTo(interruption == "delete.finalized" ? 0 : 1);
            await Assert.That(File.Exists(state.TargetPath)).IsEqualTo(interruption == "delete.intent");
            if (interruption == "delete.main_file")
            {
                foreach (var suffix in state.Sidecars)
                {
                    await Assert.That(File.Exists(state.TargetPath + suffix)).IsTrue();
                }
            }

            if (interruption is "cleanup.files" or "cleanup.clearing" or "delete.finalized")
            {
                await state.AssertFilesGoneAsync(target);
            }

            var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
            var storage = new TenantContext(state.Organization, PlanTier.Hobby, state.Project, target);
            if (interruption == "delete.finalized")
            {
                await Assert.That(async () => { _ = await engine.CreateDatabaseAsync(storage, CancellationToken.None); }).Throws<IOException>();
                await state.AssertFilesGoneAsync(target);
            }
            else
            {
                await Assert.That(async () =>
                {
                    _ = await engine.CreateDatabaseAsync(storage, CancellationToken.None);
                }).Throws<IOException>();
                await Assert.That(async () =>
                {
                    _ = await engine.CopyDatabaseAsync(storage with { DatabaseId = state.Source }, storage, CancellationToken.None);
                }).Throws<IOException>();
                await Assert.That(async () =>
                {
                    _ = await engine.DeleteDatabaseAsync(storage, CancellationToken.None);
                }).Throws<IOException>();
            }

            if (interruption == "delete.intent")
            {
                // Reusing a removed name before replay must not make the new id a cleanup target.
                armed = false;
                replacement = await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner);
                await Assert.That(replacement).IsNotEqualTo(target);
                using var data = api.DataClient(state.ApiKey);
                await state.WriteDocumentAsync(data, state.TargetName);
            }
        }

        if (interruption == "delete.intent")
        {
            // Shutdown closes real Turso sidecars; recreate crash leftovers while no host owns them.
            await state.WriteArtifactsAsync(target, sidecars: true);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            await using var api = await state.StartAsync();
            using var owner = api.As(state.User);
            await state.AssertFilesGoneAsync(target);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            if (restart == 0)
            {
                var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
                var storage = new TenantContext(state.Organization, PlanTier.Hobby, state.Project, target);
                await Assert.That(async () => { _ = await engine.CreateDatabaseAsync(storage, CancellationToken.None); }).Throws<IOException>();
                await state.AssertFilesGoneAsync(target);
            }

            await AssertReadyAsync(api);
            await state.AssertUnregisteredAsync(api, owner, target, replacement);
            if (replacement is not null)
            {
                await Assert.That(File.Exists(state.Path(replacement))).IsTrue();
                using var data = api.DataClient(state.ApiKey);
                await state.AssertDocumentAsync(data, state.TargetName);
            }

            await state.AssertPreservedAsync(api, owner);
        }

        // Removing the name claim is observable: a new database can take the old name.
        await using var final = await state.StartAsync();
        using var finalOwner = final.As(state.User);
        if (replacement is null)
        {
            replacement = await final.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, finalOwner);
            await Assert.That(replacement).IsNotEqualTo(target);
        }
    }

    [Test]
    [Arguments("management", "create.intent")]
    [Arguments("management", "create.files")]
    [Arguments("management", "create.registered")]
    [Arguments("branch", "create.intent")]
    [Arguments("branch", "create.files")]
    [Arguments("branch", "create.registered")]
    [Arguments("data", "create.intent")]
    [Arguments("data", "create.files")]
    [Arguments("data", "create.registered")]
    public async Task Interrupted_creation_preserves_only_a_committed_registration(string route, string interruption)
    {
        await using var state = new RecoveryData();
        var armed = false;
        var reached = false;
        string target;
        var committed = interruption == "create.registered";
        await using (var api = await state.StartAsync(services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            if (armed && step == interruption)
            {
                reached = true;
                throw new InvalidOperationException("interrupted at " + step);
            }
        })))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            armed = true;
            using var data = api.DataClient(state.ApiKey);
            using var response = route == "data"
                ? await data.PutAsync(Uri($"/v1/projects/{state.Project}/databases/{state.TargetName}"), null)
                : await owner.PostAsJsonAsync(
                    Uri(route == "branch" ? state.Databases + "/" + state.Source + "/branches" : state.Databases), new { name = state.TargetName });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(reached).IsTrue();
            var pending = await PendingAsync(api);
            if (committed)
            {
                await Assert.That(pending.Documents).IsEmpty();
                var page = await owner.GetFromJsonAsync<JsonElement>(Uri(state.Databases));
                target = page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == state.TargetName)
                    .GetProperty("id").GetString()!;
            }
            else
            {
                await Assert.That(pending.Documents.Count).IsEqualTo(1);
                target = pending.Documents[0].Id;
                using var intent = JsonDocument.Parse(pending.Documents[0].Json);
                await Assert.That(intent.RootElement.GetProperty("organizationId").GetString()).IsEqualTo(state.Organization);
                await Assert.That(intent.RootElement.GetProperty("projectId").GetString()).IsEqualTo(state.Project);
                await Assert.That(intent.RootElement.GetProperty("databaseId").GetString()).IsEqualTo(target);
                await Assert.That(intent.RootElement.GetProperty("plan").GetString()).IsEqualTo("hobby");
                await state.AssertUnregisteredAsync(api, owner, target);
            }

            await Assert.That(File.Exists(state.Path(target))).IsEqualTo(interruption != "create.intent");
        }

        if (!committed)
        {
            await state.WriteArtifactsAsync(target, sidecars: true);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            await using var api = await state.StartAsync();
            using var owner = api.As(state.User);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            if (committed)
            {
                await Assert.That(File.Exists(state.Path(target))).IsTrue();
            }
            else
            {
                await state.AssertFilesGoneAsync(target);
            }

            await AssertReadyAsync(api);
            await state.AssertPreservedAsync(api, owner);
            if (committed)
            {
                using var read = await owner.GetAsync(Uri(state.Databases + "/" + target));
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(File.Exists(state.Path(target))).IsTrue();
                using var data = api.DataClient(state.ApiKey);
                if (route == "branch")
                {
                    await state.AssertDocumentAsync(data, state.TargetName);
                }
                else
                {
                    await state.WriteDocumentAsync(data, state.TargetName);
                    await state.AssertDocumentAsync(data, state.TargetName);
                }
            }
            else
            {
                await state.AssertUnregisteredAsync(api, owner, target);
            }
        }
    }

    [Test]
    [Arguments("management")]
    [Arguments("branch")]
    [Arguments("data")]
    [Arguments("service")]
    public async Task A_failure_after_registration_commit_preserves_data_and_releases_the_creation_reservation(string route)
    {
        await using var state = new RecoveryData();
        var armed = false;
        TenantContext? captured = null;
        var injected = new InvalidOperationException("interrupted after registration commit");
        string target;
        await using (var api = await state.StartAsync(services => services
            .AddSingleton<TimeProvider>(new FakeTimeProvider(TimeProvider.System.GetUtcNow()))
            .AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
        {
            if (armed && step == "create.committed")
            {
                captured = storage;
                throw injected;
            }
        })))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            using var data = api.DataClient(state.ApiKey);
            armed = true;
            if (route == "service")
            {
                InvalidOperationException? observed = null;
                try
                {
                    _ = await api.Server.Services.GetRequiredService<IManagementService>().CreateDatabaseAsync(
                        state.User, state.Organization, state.Project, state.TargetName, CancellationToken.None);
                }
                catch (InvalidOperationException exception)
                {
                    observed = exception;
                }

                await Assert.That(ReferenceEquals(observed, injected)).IsTrue();
            }
            else
            {
                using var response = route == "data"
                    ? await data.PutAsync(Uri($"/v1/projects/{state.Project}/databases/{state.TargetName}"), null)
                    : await owner.PostAsJsonAsync(
                        Uri(route == "branch" ? state.Databases + "/" + state.Source + "/branches" : state.Databases), new { name = state.TargetName });
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            }

            await Assert.That(captured).IsNotNull();
            var storage = captured!;
            target = storage.DatabaseId!;
            await Assert.That(storage.OrganizationId).IsEqualTo(state.Organization);
            await Assert.That(storage.ProjectId).IsEqualTo(state.Project);
            await Assert.That(storage.Plan).IsEqualTo(PlanTier.Hobby);
            await Assert.That(await CatalogAsync(api, $"projects/{state.Project}/databases", target)).IsNotNull();
            await Assert.That(await CatalogAsync(api, $"projects/{state.Project}/database_names", state.TargetName))
                .IsEqualTo(JsonSerializer.Serialize(target));
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await Assert.That(File.Exists(state.Path(target))).IsTrue();
            using (var registered = await owner.GetAsync(Uri(state.Databases + "/" + target)))
            {
                await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            if (route != "branch")
            {
                await state.WriteDocumentAsync(data, state.TargetName);
            }

            await state.AssertDocumentAsync(data, state.TargetName);
            await state.AssertPreservedAsync(api, owner);
            armed = false;
            var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
            // A leaked reservation rejects this existing database despite its committed catalog row.
            await Assert.That(await engine.CreateDatabaseAsync(storage, CancellationToken.None)).IsFalse();
            using var deleted = await owner.DeleteAsync(Uri(state.Databases + "/" + target));
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await state.AssertFilesGoneAsync(target);
            await state.AssertUnregisteredAsync(api, owner, target);
            await Assert.That(async () => { _ = await engine.CreateDatabaseAsync(storage, CancellationToken.None); }).Throws<IOException>();
            await state.AssertFilesGoneAsync(target);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            await using var api = await state.StartAsync();
            using var owner = api.As(state.User);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await state.AssertFilesGoneAsync(target);
            await state.AssertUnregisteredAsync(api, owner, target);
            await AssertReadyAsync(api);
            await AssertUnknownCountAsync(api, 0);
            await state.AssertPreservedAsync(api, owner);
        }
    }

    [Test]
    [Arguments("http")]
    [Arguments("service")]
    [Arguments("cancelled-create")]
    public async Task A_failure_after_cleanup_commit_releases_the_destination_and_preserves_the_original_exception(string route)
    {
        await using var state = new RecoveryData();
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        TenantContext? captured = null;
        var injected = new InvalidOperationException("interrupted after cleanup commit");
        var target = string.Empty;
        string replacement;
        await using (var api = await state.StartAsync(services => services
            .AddSingleton<TimeProvider>(new FakeTimeProvider(TimeProvider.System.GetUtcNow()))
            .AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
        {
            if (armed && step == "create.intent" && route == "cancelled-create")
            {
                cancellation.Cancel();
            }

            if (armed && step == "cleanup.committed")
            {
                captured = storage;
                throw injected;
            }
        })))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            if (route != "cancelled-create")
            {
                target = await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner);
                using var data = api.DataClient(state.ApiKey);
                await state.WriteDocumentAsync(data, state.TargetName);
                await state.WriteArtifactsAsync(target, sidecars: false);
            }

            armed = true;
            if (route == "http")
            {
                using var deleted = await owner.DeleteAsync(Uri(state.Databases + "/" + target));
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            }
            else
            {
                var management = api.Server.Services.GetRequiredService<IManagementService>();
                InvalidOperationException? observed = null;
                try
                {
                    if (route == "cancelled-create")
                    {
                        _ = await management.CreateDatabaseAsync(state.User, state.Organization, state.Project, state.TargetName, cancellation.Token);
                    }
                    else
                    {
                        _ = await management.DeleteDatabaseAsync(state.User, state.Organization, state.Project, target, CancellationToken.None);
                    }
                }
                catch (InvalidOperationException exception)
                {
                    observed = exception;
                }

                await Assert.That(ReferenceEquals(observed, injected)).IsTrue();
            }

            await Assert.That(captured).IsNotNull();
            var storage = captured!;
            if (route == "cancelled-create")
            {
                await Assert.That(cancellation.IsCancellationRequested).IsTrue();
                target = storage.DatabaseId!;
            }

            await Assert.That(storage).IsEqualTo(new TenantContext(state.Organization, PlanTier.Hobby, state.Project, target));
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await state.AssertFilesGoneAsync(target);
            await state.AssertUnregisteredAsync(api, owner, target);
            await state.AssertPreservedAsync(api, owner);
            var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
            if (route == "cancelled-create")
            {
                await Assert.That(await engine.CreateDatabaseAsync(storage, CancellationToken.None)).IsTrue();
                await Assert.That(await engine.DeleteDatabaseAsync(storage, CancellationToken.None)).IsTrue();
                await Assert.That(await engine.CopyDatabaseAsync(storage with { DatabaseId = state.Source }, storage, CancellationToken.None)).IsTrue();
                await Assert.That(await engine.DeleteDatabaseAsync(storage, CancellationToken.None)).IsTrue();
            }
            else
            {
                await Assert.That(async () => { _ = await engine.CreateDatabaseAsync(storage, CancellationToken.None); }).Throws<IOException>();
                await Assert.That(async () => { _ = await engine.CopyDatabaseAsync(storage with { DatabaseId = state.Source }, storage, CancellationToken.None); }).Throws<IOException>();
            }
            await state.AssertFilesGoneAsync(target);

            armed = false;
            replacement = await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner);
            await Assert.That(replacement).IsNotEqualTo(target);
            using var replacementData = api.DataClient(state.ApiKey);
            await state.WriteDocumentAsync(replacementData, state.TargetName);
            await state.AssertDocumentAsync(replacementData, state.TargetName);
            await state.AssertUnregisteredAsync(api, owner, target, replacement);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        }

        for (var restart = 0; restart < 2; restart++)
        {
            await using var api = await state.StartAsync();
            using var owner = api.As(state.User);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await state.AssertFilesGoneAsync(target);
            await state.AssertUnregisteredAsync(api, owner, target, replacement);
            await Assert.That(File.Exists(state.Path(replacement))).IsTrue();
            using var data = api.DataClient(state.ApiKey);
            await state.AssertDocumentAsync(data, state.TargetName);
            await AssertReadyAsync(api);
            await AssertUnknownCountAsync(api, 0);
            await state.AssertPreservedAsync(api, owner);
        }
    }

    [Test]
    public async Task A_failed_startup_leaves_the_intent_for_the_next_restart()
    {
        await using var state = new RecoveryData();
        var armed = false;
        string target;
        await using (var api = await state.StartAsync(services => services
            .AddSingleton<TimeProvider>(new FakeTimeProvider(TimeProvider.System.GetUtcNow()))
            .AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            if (armed && step == "delete.intent")
            {
                throw new InvalidOperationException("interrupted deletion");
            }
        })))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            target = await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner);
            armed = true;
            using var deleted = await owner.DeleteAsync(Uri(state.Databases + "/" + target));
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        }

        var reached = false;
        await Assert.That(async () =>
        {
            await using var failed = await state.StartAsync(services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == "recovery.intent")
                {
                    reached = true;
                    throw new InvalidOperationException("interrupted startup recovery");
                }
            }));
        }).Throws<InvalidOperationException>();
        await Assert.That(reached).IsTrue();
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();
        await Assert.That(File.Exists(state.Path(target))).IsTrue();
        // No public listener exists after failed startup; inspect the persisted catalog with the existing helper.
        await Assert.That(await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db")))
            .Contains(line => line.StartsWith("pending_database_cleanup|" + target + "|", StringComparison.Ordinal));

        for (var restart = 0; restart < 2; restart++)
        {
            await using var api = await state.StartAsync();
            using var owner = api.As(state.User);
            await state.AssertFilesGoneAsync(target);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await AssertReadyAsync(api);
            await state.AssertUnregisteredAsync(api, owner, target);
            await state.AssertPreservedAsync(api, owner);
        }
    }

    [Test]
    public async Task Unknown_files_are_preserved_byte_for_byte_logged_and_counted()
    {
        await using var state = new RecoveryData();
        var unregistered = EntityIds.New().ToString();
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
            // A real database with no catalog entry must remain available for operator recovery.
            await api.Server.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(
                new TenantContext(state.Organization, PlanTier.Hobby, state.Project, unregistered), CancellationToken.None);
        }

        var paths = new[]
        {
            state.Path(unregistered),
            state.Path(unregistered) + ".key",
            // Matching ids under another organization do not make this copy registered.
            System.IO.Path.Combine(state.Directory, EntityIds.New().ToString(), state.Project, state.Source + ".db"),
            System.IO.Path.Combine(state.Directory, state.Organization, state.Project, "operator-notes.txt"),
            state.Path(EntityIds.New().ToString()) + "-wal",
            System.IO.Path.Combine(state.Directory, state.Organization, state.Project, ".unrecognized.creating"),
            System.IO.Path.Combine(state.Directory, state.Organization, "other", "nested", "archive.db"),
            System.IO.Path.Combine(state.Directory, "operator-notes.txt"),
        };
        foreach (var path in paths.Skip(2))
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, state.Faker.Random.Bytes(73));
        }

        var link = System.IO.Path.Combine(state.Directory, "operator-notes.link");
        File.CreateSymbolicLink(link, "operator-notes.txt");
        paths = [.. paths, link];
        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            before[path] = await File.ReadAllBytesAsync(path);
        }

        var directories = new[]
        {
            state.Path(EntityIds.New().ToString()),
            state.Path(EntityIds.New().ToString()) + "-wal",
            System.IO.Path.Combine(state.Directory, state.Organization, state.Project,
                // A registered id still cannot make an artifact directory registered storage.
                $".{state.Source}.{EntityIds.New()}.creating"),
        };
        foreach (var directory in directories)
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        var ordinaryDirectory = System.IO.Path.Combine(state.Directory, state.Organization, state.Project, "operator-empty");
        System.IO.Directory.CreateDirectory(ordinaryDirectory);

        // Keep a second live host with a different count: process-wide meter leakage must fail this test.
        await using var other = new RecoveryData();
        await using (var prepared = await other.StartAsync())
        {
            await other.SeedAsync(prepared);
        }

        var otherUnknown = System.IO.Path.Combine(other.Directory, other.Organization, other.Project, "notes.txt");
        await File.WriteAllBytesAsync(otherUnknown, other.Faker.Random.Bytes(17));
        await using var otherApi = await other.StartAsync();
        DateTimeOffset? firstSeen = null;
        for (var restart = 0; restart < 2; restart++)
        {
            var logs = new CapturedLogs();
            await using var api = await state.StartAsync(services => services.AddSingleton<ILoggerProvider>(logs));
            using var owner = api.As(state.User);
            var observation = (await ObservationAsync(api, new TenantContext(state.Organization, PlanTier.Hobby, state.Project, unregistered)))!;
            firstSeen ??= observation.FirstSeen;
            await Assert.That(observation.FirstSeen).IsEqualTo(firstSeen.Value);
            var metrics = await api.Server.Client.GetStringAsync(Uri("/metrics"));
            await Assert.That(double.Parse(metrics.Split('\n').Single(line => line.StartsWith("terrabase_storage_unknown_bytes{", StringComparison.Ordinal)).Split(' ')[1], CultureInfo.InvariantCulture)).IsGreaterThan(0);
            foreach (var (path, bytes) in before)
            {
                await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)).IsTrue();
                await AssertFileLoggedAsync(api, logs, path);
            }

            foreach (var directory in directories)
            {
                await Assert.That(System.IO.Directory.Exists(directory)).IsTrue();
                await Assert.That(System.IO.Directory.EnumerateFileSystemEntries(directory)).IsEmpty();
                await AssertFileLoggedAsync(api, logs, directory);
            }

            await Assert.That(System.IO.Directory.Exists(ordinaryDirectory)).IsTrue();
            await Assert.That(System.IO.Directory.EnumerateFileSystemEntries(ordinaryDirectory)).IsEmpty();
            await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo("operator-notes.txt");
            await AssertUnknownCountAsync(api, paths.Length + directories.Length);
            await AssertUnknownCountAsync(otherApi, 1);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await state.AssertPreservedAsync(api, owner);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("-wal")]
    [Arguments("-shm")]
    [Arguments("-journal")]
    public async Task Root_system_filename_directories_refuse_startup_and_preserve_inventory_on_every_restart(string suffix)
    {
        await using var state = new RecoveryData();
        await TerraBaseTestServer.InitializeDataDirectoryAsync(state.Directory);
        var systemPath = System.IO.Path.Combine(state.Directory, "_system.db");
        await Assert.That(File.Exists(systemPath)).IsFalse();
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();

        // The real engine prepared the layout without creating a system store.
        var directory = systemPath + suffix;
        System.IO.Directory.CreateDirectory(directory);
        var organizationDirectory = System.IO.Path.Combine(state.Directory, EntityIds.New().ToString());
        var ordinaryDirectories = new[]
        {
            System.IO.Path.Combine(state.Directory, "operator-empty"),
            organizationDirectory,
            System.IO.Path.Combine(organizationDirectory, EntityIds.New().ToString()),
        };
        foreach (var ordinary in ordinaryDirectories)
        {
            System.IO.Directory.CreateDirectory(ordinary);
        }

        if (suffix.Length == 0)
        {
            // Prove the main-file directory alone refuses startup, before adding any sidecar controls.
            await Assert.That(async () =>
            {
                await using var failed = await state.StartAsync();
            }).Throws<IOException>();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();
        }

        // Regular reserved sidecars remain excluded, even next to a directory with another reserved name.
        var controls = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var sidecar in state.Sidecars.Where(sidecar => sidecar != suffix))
        {
            var path = systemPath + sidecar;
            var bytes = state.Faker.Random.Bytes(31);
            await File.WriteAllBytesAsync(path, bytes);
            controls.Add(path, bytes);
        }

        // Resolving the hosted ImportRunner constructs ImportStaging before any hosted service starts.
        var importDirectory = System.IO.Path.Combine(state.Directory, ".import");
        var entries = System.IO.Directory.GetFileSystemEntries(state.Directory, "*", SearchOption.AllDirectories)
            .Append(importDirectory).Distinct(StringComparer.Ordinal).ToArray();
        var lockPath = System.IO.Path.Combine(state.Directory, ".terrabase.lock");
        var layout = await File.ReadAllBytesAsync(lockPath);
        for (var restart = 0; restart < 2; restart++)
        {
            var logs = new CapturedLogs();
            await Assert.That(async () =>
            {
                await using var failed = await state.StartAsync(services => services.AddSingleton<ILoggerProvider>(logs));
            }).Throws<IOException>();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();
            await Assert.That(File.Exists(systemPath)).IsFalse();
            await Assert.That((await File.ReadAllBytesAsync(lockPath)).SequenceEqual(layout)).IsTrue();
            await Assert.That(System.IO.Directory.Exists(importDirectory)).IsTrue();
            await Assert.That(System.IO.Directory.EnumerateFileSystemEntries(importDirectory)).IsEmpty();
            var actualEntries = System.IO.Directory.GetFileSystemEntries(state.Directory, "*", SearchOption.AllDirectories);
            await Assert.That(actualEntries).IsEquivalentTo(entries).Because(
                "only the fixture entries and empty import staging directory may exist; actual entries: "
                + string.Join(", ", actualEntries.Order(StringComparer.Ordinal)));
            // Recovery refuses startup before management inventory or its serving metrics can run.
            await Assert.That(logs.Messages.Where(message => message.StartsWith(
                "Warning: Preserved database file without a catalog registration: ", StringComparison.Ordinal)).ToArray()).IsEmpty();

            // Inspect the real engine's inventory without opening SYSTEM or starting hosted services.
            var options = new TerraBaseOptions { DataDirectory = state.Directory, MemoryBudget = 64 * 1024 * 1024, StorageThreads = 1, StorageQueue = 16 };
            var services = new ServiceCollection();
            services.AddTerraBaseEngine(options);
            services.AddSingleton<INativeTemporaryStorage, TestNativeTemporaryStorage>();
            services.AddKeyedSingleton<IMasterKey, TestMasterKey>(EngineExtensions.DatabaseMasterKey);
            await using var provider = services.BuildServiceProvider();
            var engine = provider.GetRequiredService<IStorageEngine>();
            var inventory = engine.EnumerateDatabaseFiles().ToArray();
            await Assert.That(inventory.Length).IsEqualTo(1);
            await Assert.That(inventory[0].Path).IsEqualTo(directory);
            await Assert.That(inventory[0].Database).IsNull();
            await Assert.That(engine.SystemDatabaseExists).IsFalse();

            await Assert.That(System.IO.Directory.Exists(directory)).IsTrue();
            await Assert.That(System.IO.Directory.EnumerateFileSystemEntries(directory)).IsEmpty();
            foreach (var ordinary in ordinaryDirectories)
            {
                await Assert.That(System.IO.Directory.Exists(ordinary)).IsTrue();
            }

            foreach (var (path, bytes) in controls)
            {
                await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)).IsTrue();
            }

            await Assert.That(engine.EnumerateDatabaseFiles().Select(file => file.Path)).IsEquivalentTo([directory]);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("-wal")]
    [Arguments("-shm")]
    [Arguments("-journal")]
    public async Task Startup_rejects_system_database_links_without_touching_the_external_target(string suffix)
    {
        await using var state = new RecoveryData();
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
        }

        await using var external = new RecoveryData();
        var externalOptions = new TerraBaseOptions { DataDirectory = external.Directory, MemoryBudget = 64 * 1024 * 1024, StorageThreads = 1, StorageQueue = 16 };
        var externalServices = new ServiceCollection();
        externalServices.AddSingleton(externalOptions);
        externalServices.AddLogging();
        externalServices.AddTerraBaseEngine(externalOptions);
        externalServices.AddSingleton<INativeTemporaryStorage, TestNativeTemporaryStorage>();
        externalServices.AddKeyedSingleton<IMasterKey, TestMasterKey>(EngineExtensions.DatabaseMasterKey);
        await using (var provider = externalServices.BuildServiceProvider())
        {
            // A real store with an old catalog version would be changed by management migration.
            var system = await provider.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None);
            await transaction.PutAsync("catalog", "version", Encoding.UTF8.GetBytes("\"3\""), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var target = System.IO.Path.Combine(external.Directory, "_system.db");
        var bytes = await File.ReadAllBytesAsync(target);
        var externalFiles = System.IO.Directory.GetFiles(external.Directory).Order(StringComparer.Ordinal).ToArray();
        var systemPath = System.IO.Path.Combine(state.Directory, "_system.db");
        var catalog = await File.ReadAllBytesAsync(systemPath);
        var source = await File.ReadAllBytesAsync(state.Path(state.Source));
        var neighbor = await File.ReadAllBytesAsync(state.Path(state.Neighbor));
        var link = systemPath + suffix;
        var backup = link + ".backup";
        var hadFile = File.Exists(link);
        if (hadFile)
        {
            File.Move(link, backup);
        }

        try
        {
            File.CreateSymbolicLink(link, target);
            var options = new TerraBaseOptions { DataDirectory = state.Directory, MemoryBudget = 64 * 1024 * 1024, StorageThreads = 1, StorageQueue = 16 };
            var services = new ServiceCollection();
            services.AddSingleton(options);
            services.AddLogging();
            services.AddTerraBaseEngine(options);
            services.AddSingleton<INativeTemporaryStorage, TestNativeTemporaryStorage>();
            services.AddKeyedSingleton<IMasterKey, TestMasterKey>(EngineExtensions.DatabaseMasterKey);
            await using (var provider = services.BuildServiceProvider())
            {
                // No management existence probe: the default engine must reject the link itself.
                var engine = provider.GetRequiredService<IStorageEngine>();
                await Assert.That(async () =>
                {
                    _ = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
                }).Throws<IOException>();
            }

            for (var restart = 0; restart < 2; restart++)
            {
                await Assert.That(async () =>
                {
                    await using var failed = await state.StartAsync();
                }).Throws<IOException>();
                await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();
                await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
                await Assert.That((await File.ReadAllBytesAsync(target)).SequenceEqual(bytes)).IsTrue();
                await Assert.That(System.IO.Directory.GetFiles(external.Directory).Order(StringComparer.Ordinal).ToArray())
                    .IsEquivalentTo(externalFiles);
                await Assert.That((await File.ReadAllBytesAsync(suffix.Length == 0 ? backup : systemPath)).SequenceEqual(catalog)).IsTrue();
                await Assert.That((await File.ReadAllBytesAsync(state.Path(state.Source))).SequenceEqual(source)).IsTrue();
                await Assert.That((await File.ReadAllBytesAsync(state.Path(state.Neighbor))).SequenceEqual(neighbor)).IsTrue();
            }
        }
        finally
        {
            // Delete only our link, never its target; restore the original main file or sidecar.
            if (new FileInfo(link).LinkTarget == target)
            {
                File.Delete(link);
            }

            if (hadFile)
            {
                File.Move(backup, link);
            }
        }

        await using var recovered = await state.StartAsync();
        await AssertReadyAsync(recovered);
        await Assert.That((await PendingAsync(recovered)).Documents).IsEmpty();
        using var owner = recovered.As(state.User);
        await state.AssertPreservedAsync(recovered, owner);
        await Assert.That((await File.ReadAllBytesAsync(target)).SequenceEqual(bytes)).IsTrue();
        await Assert.That(System.IO.Directory.GetFiles(external.Directory).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(externalFiles);
        await AssertUnknownCountAsync(recovered, 0);
    }

    [Test]
    [Arguments("-wal", false)]
    [Arguments("-shm", false)]
    [Arguments("-journal", false)]
    [Arguments("-wal", true)]
    [Arguments("-shm", true)]
    [Arguments("-journal", true)]
    public async Task Tenant_opening_rejects_linked_sidecars_without_touching_external_data(string suffix, bool dangling)
    {
        await using var state = new RecoveryData();
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
        }

        await using var external = new RecoveryData();
        var target = System.IO.Path.Combine(external.Directory, "operator-data");
        var bytes = state.Faker.Random.Bytes(73);
        if (!dangling)
        {
            await File.WriteAllBytesAsync(target, bytes);
        }

        var sourceBytes = await File.ReadAllBytesAsync(state.Path(state.Source));
        var link = state.Path(state.Source) + suffix;
        var backup = System.IO.Path.Combine(external.Directory, "sidecar-backup");
        var hadFile = File.Exists(link);
        if (hadFile)
        {
            File.Move(link, backup);
        }

        try
        {
            File.CreateSymbolicLink(link, target);
            await using var api = await state.StartAsync();
            await AssertReadyAsync(api);
            await AssertUnknownCountAsync(api, 1);
            var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
            var source = new TenantContext(state.Organization, PlanTier.Hobby, state.Project, state.Source);
            var destination = source with { DatabaseId = EntityIds.New().ToString() };
            await Assert.That(async () =>
            {
                _ = await engine.OpenDatabaseAsync(source, CancellationToken.None);
            }).Throws<IOException>();
            await Assert.That(async () =>
            {
                _ = await engine.CopyDatabaseAsync(source, destination, CancellationToken.None);
            }).Throws<IOException>();
            await state.AssertFilesGoneAsync(destination.DatabaseId!);

            using var owner = api.As(state.User);
            using var branch = await owner.PostAsJsonAsync(
                Uri(state.Databases + "/" + state.Source + "/branches"), new { name = state.TargetName });
            await Assert.That(branch.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            using var data = api.DataClient(state.ApiKey);
            using var read = await data.GetAsync(Uri(
                $"/v1/projects/{state.Project}/databases/source/collections/people/documents/by-key?id={EntityIds.New()}"));
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await state.AssertUnregisteredAsync(api, owner, destination.DatabaseId!);
            await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
            await Assert.That((await File.ReadAllBytesAsync(state.Path(state.Source))).SequenceEqual(sourceBytes)).IsTrue();
            if (dangling)
            {
                await Assert.That(File.Exists(target)).IsFalse();
            }
            else
            {
                await Assert.That((await File.ReadAllBytesAsync(target)).SequenceEqual(bytes)).IsTrue();
            }
        }
        finally
        {
            if (new FileInfo(link).LinkTarget == target)
            {
                File.Delete(link);
            }

            if (hadFile)
            {
                File.Move(backup, link);
            }
        }

        await using var recovered = await state.StartAsync();
        using var recoveredOwner = recovered.As(state.User);
        await state.AssertPreservedAsync(recovered, recoveredOwner);
        await AssertUnknownCountAsync(recovered, 0);
    }

    [Test]
    public async Task Inventory_cancellation_stops_traversal_when_only_excluded_directories_remain()
    {
        await using var state = new RecoveryData();
        await using var api = await state.StartAsync();
        await AssertReadyAsync(api);
        var note = System.IO.Path.Combine(state.Directory, "operator-notes.txt");
        await File.WriteAllBytesAsync(note, state.Faker.Random.Bytes(17));
        var nested = System.IO.Path.Combine(state.Directory, "ordinary-directory", "nested-empty-directory");
        System.IO.Directory.CreateDirectory(nested);
        using var cancellation = new CancellationTokenSource();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        using var inventory = engine.EnumerateDatabaseFiles(cancellation.Token).GetEnumerator();
        await Assert.That(inventory.MoveNext()).IsTrue();
        await Assert.That(inventory.Current.Path).IsEqualTo(note);
        // Recursion visits the nested directory after yielding the sole included root entry, regardless of entry order.
        cancellation.Cancel();
        await Assert.That(inventory.MoveNext).Throws<OperationCanceledException>();
        await Assert.That(System.IO.Directory.Exists(nested)).IsTrue();
        await Assert.That(File.Exists(note)).IsTrue();
        await AssertReadyAsync(api);
    }

    [Test]
    [Arguments("organization")]
    [Arguments("project")]
    [Arguments("database")]
    public async Task An_unsupported_catalog_version_preserves_files_without_deserializing_future_rows(string row)
    {
        await using var state = new RecoveryData();
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
        }

        var (collection, id) = row switch
        {
            "organization" => ("organizations", state.Organization),
            "project" => ($"organizations/{state.Organization}/projects", state.Project),
            _ => ($"projects/{state.Project}/databases", state.Source),
        };
        // Only replace rows after shutdown: the seeded host's usage writer still understands the current format.
        // Valid future JSON: an unknown enum for organizations, an incompatible date for projects/databases.
        var json = $$$"""{"id":"{{{id}}}","plan":"future","createdAt":{"unixNanos":"1"}}""";
        var systemPath = System.IO.Path.Combine(state.Directory, "_system.db");
        await DocumentFiles.UpdateAsync(systemPath, ("catalog", "version", "\"99\""), (collection, id, json));

        // Add crash sidecars only after the seeded host has closed its real tenant stores.
        foreach (var database in new[] { state.Source, state.Neighbor })
        {
            foreach (var suffix in state.Sidecars.Where(suffix => suffix != ".key"))
            {
                await File.WriteAllBytesAsync(state.Path(database) + suffix, state.Faker.Random.Bytes(31));
            }
        }

        var paths = System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(state.Path(state.Source))!);
        await Assert.That(paths.Length).IsEqualTo(10);
        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            before[path] = await File.ReadAllBytesAsync(path);
        }

        var catalog = await DocumentFiles.ReadAsync(systemPath);
        for (var restart = 0; restart < 2; restart++)
        {
            var logs = new CapturedLogs();
            var lookups = new ConcurrentQueue<TenantContext>();
            await using (var api = await state.StartAsync(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
                {
                    if (step == "audit.registration")
                    {
                        lookups.Enqueue(database);
                    }
                });
            }))
            {
                await Assert.That(lookups).IsEmpty();
                await AssertReadyAsync(api);
                await AssertUnknownCountAsync(api, paths.Length);
                using var owner = api.As(state.User);
                using var list = await owner.GetAsync(Uri("/v1/management/organizations"));
                await Assert.That(list.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                using var create = await owner.PostAsJsonAsync(Uri(state.Databases), new { name = state.TargetName });
                await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                await Assert.That(await CatalogAsync(api, "catalog", "version")).IsEqualTo("\"99\"");
                foreach (var (path, bytes) in before)
                {
                    await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)).IsTrue();
                    await AssertFileLoggedAsync(api, logs, path);
                }
            }

            await Assert.That(await DocumentFiles.ReadAsync(systemPath)).IsEquivalentTo(catalog);
            await Assert.That(System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(state.Path(state.Source))!)).IsEquivalentTo(paths);
            foreach (var (path, bytes) in before)
            {
                await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)).IsTrue();
            }
        }
    }

    [Test]
    public async Task An_unsupported_catalog_preserves_an_interrupted_creation_and_refuses_new_creations()
    {
        await using var state = new RecoveryData();
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
        }

        string target;
        string intent;
        await using (var api = await state.StartAsync(services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            if (step == "create.intent")
            {
                throw new InvalidOperationException("interrupted creation");
            }
        })))
        {
            await Assert.That(await CatalogAsync(api, "catalog", "version")).IsEqualTo("\"1\"");
            using var owner = api.As(state.User);
            using var created = await owner.PostAsJsonAsync(Uri(state.Databases), new { name = state.TargetName });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(await CatalogAsync(api, "catalog", "version")).IsEqualTo("\"1\"");
            var pending = await PendingAsync(api);
            await Assert.That(pending.Documents.Count).IsEqualTo(1);
            target = pending.Documents[0].Id;
            intent = Encoding.UTF8.GetString(pending.Documents[0].Json.Span);
        }

        var systemPath = System.IO.Path.Combine(state.Directory, "_system.db");
        await DocumentFiles.UpdateAsync(systemPath, ("catalog", "version", "\"3\""));
        var catalog = await DocumentFiles.ReadAsync(systemPath);
        var source = await File.ReadAllBytesAsync(state.Path(state.Source));
        var neighbor = await File.ReadAllBytesAsync(state.Path(state.Neighbor));
        for (var restart = 0; restart < 2; restart++)
        {
            await using (var api = await state.StartAsync())
            {
                using var owner = api.As(state.User);
                await ErrorAssert.ErrorAsync(owner.PostAsJsonAsync(Uri(state.Databases), new { name = state.TargetName }),
                    HttpStatusCode.ServiceUnavailable, "unavailable", "the management catalog has an old or unsupported format; discard the affected data");
                var pending = await PendingAsync(api);
                await Assert.That(pending.Documents.Count).IsEqualTo(1);
                await Assert.That(pending.Documents[0].Id).IsEqualTo(target);
                await Assert.That(Encoding.UTF8.GetString(pending.Documents[0].Json.Span)).IsEqualTo(intent);
                await Assert.That(await CatalogAsync(api, "catalog", "version")).IsEqualTo("\"3\"");
                await AssertReadyAsync(api);
            }

            await Assert.That(await DocumentFiles.ReadAsync(systemPath)).IsEquivalentTo(catalog);
            await Assert.That((await File.ReadAllBytesAsync(state.Path(state.Source))).SequenceEqual(source)).IsTrue();
            await Assert.That((await File.ReadAllBytesAsync(state.Path(state.Neighbor))).SequenceEqual(neighbor)).IsTrue();
            await Assert.That(File.Exists(state.Path(target))).IsFalse();
        }

        await DocumentFiles.UpdateAsync(systemPath, ("catalog", "version", "\"1\""));
        await using var recovered = await state.StartAsync();
        using var recoveredOwner = recovered.As(state.User);
        await Assert.That((await PendingAsync(recovered)).Documents).IsEmpty();
        await state.AssertFilesGoneAsync(target);
        await AssertReadyAsync(recovered);
        await state.AssertUnregisteredAsync(recovered, recoveredOwner, target);
        await state.AssertPreservedAsync(recovered, recoveredOwner);
        await recovered.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, recoveredOwner);
        await Assert.That(await CatalogAsync(recovered, "catalog", "version")).IsEqualTo("\"1\"");
    }

    [Test]
    public async Task Registration_clearing_a_paged_intent_preserves_the_database_during_startup()
    {
        await using var state = new RecoveryData();
        var registration = await UnregisterSourceWithCleanupIntentAsync(state);
        var hook = new UsageCommitHook();
        var reached = false;
        await using var api = await state.StartAsync(services =>
        {
            hook.Install(services);
            services.AddSingleton<DatabaseLifecycleCheckpoint>(provider => (step, storage) =>
            {
                if (step == "recovery.intent" && storage.DatabaseId == state.Source)
                {
                    // The page's transaction is closed; commit both facts before recovery opens its next snapshot.
                    hook.BeforeCatalogBegin = async (_, token) =>
                    {
                        hook.BeforeCatalogBegin = null;
                        var system = await provider.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
                        await using var transaction = await system.BeginTransactionAsync(write: true, token);
                        await transaction.PutAsync($"projects/{state.Project}/databases", state.Source, registration.Json, token);
                        await transaction.DeleteAsync("pending_database_cleanup", state.Source, token);
                        await transaction.CommitAsync(token);
                        reached = true;
                    };
                }
            });
        });

        await Assert.That(reached).IsTrue();
        await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        using var owner = api.As(state.User);
        await state.AssertPreservedAsync(api, owner);
        await AssertReadyAsync(api);
    }

    [Test]
    [Arguments("recovery.intent")]
    [Arguments("cleanup.clearing")]
    public async Task Recovery_does_not_clear_a_replacement_of_its_paged_intent(string replacementStep)
    {
        await using var state = new RecoveryData();
        _ = await UnregisterSourceWithCleanupIntentAsync(state);
        var source = await File.ReadAllBytesAsync(state.Path(state.Source));
        var hook = new UsageCommitHook();
        var replaced = false;
        var revisited = false;
        List<string>? replacementCatalog = null;
        await using (var api = await state.StartAsync(services =>
        {
            // Startup defers the replacement to convergence; keep that later pass outside this test.
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
            hook.Install(services);
            services.AddSingleton<DatabaseLifecycleCheckpoint>(provider => (step, storage) =>
            {
                if (storage.DatabaseId != state.Source)
                {
                    return;
                }

                if (step == "recovery.intent" && replaced)
                {
                    // A replacement belongs to a later convergence pass, never this startup page.
                    revisited = true;
                    throw new IOException("replacement intent awaits its own recovery");
                }

                if (step == replacementStep)
                {
                    // Arm before either the recovery read or cleanup's write (including a durable begin).
                    hook.BeforeCatalogBegin = async (_, token) =>
                    {
                        // The replacement's own transaction must not reenter this callback.
                        hook.BeforeCatalogBegin = null;
                        var system = await provider.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
                        long version;
                        await using (var transaction = await system.BeginTransactionAsync(write: true, token))
                        {
                            var pending = (await transaction.GetAsync("pending_database_cleanup", state.Source, token))!;
                            // PUT mints a fresh state stamp even for identical JSON; version distinguishes the intent.
                            version = await transaction.PutAsync("pending_database_cleanup", state.Source, pending.Json, token);
                            await Assert.That(version).IsGreaterThan(pending.Version);
                            await transaction.CommitAsync(token);
                        }

                        await using (var transaction = await system.BeginTransactionAsync(write: false, token))
                        {
                            var replacement = (await transaction.GetAsync("pending_database_cleanup", state.Source, token))!;
                            await Assert.That(replacement.Version).IsEqualTo(version);
                        }

                        replacementCatalog = await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db"));
                        replaced = true;
                    };
                }
            });
        }))
        {
            await Assert.That((await PendingAsync(api)).Documents.Count).IsEqualTo(1);
            await AssertReadyAsync(api);
        }

        await Assert.That(replaced).IsTrue();
        await Assert.That(revisited).IsFalse();
        await Assert.That(await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db"))).IsEquivalentTo(replacementCatalog!);
        if (replacementStep == "recovery.intent")
        {
            await Assert.That((await File.ReadAllBytesAsync(state.Path(state.Source))).SequenceEqual(source)).IsTrue();
        }

        await Assert.That(File.Exists(state.Path(state.Neighbor))).IsTrue();
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();
    }

    [Test]
    public async Task An_intent_naming_a_different_database_than_its_key_refuses_startup_without_deleting_data()
    {
        await using var state = new RecoveryData();
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
            var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None);
            // A valid shared creation intent beside a registration is preserved (CatalogCleanupRaceTests).
            // Metadata naming a different database from its key remains corrupt and must never authorize cleanup.
            var storage = new TenantContext(state.Organization, PlanTier.Hobby, state.Project, state.Neighbor);
            await transaction.PutAsync("pending_database_cleanup", state.Source,
                JsonSerializer.SerializeToUtf8Bytes(storage, TerraBaseJsonContext.Default.TenantContext), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var source = await File.ReadAllBytesAsync(state.Path(state.Source));
        var neighbor = await File.ReadAllBytesAsync(state.Path(state.Neighbor));
        var catalog = await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db"));
        await Assert.That(async () =>
        {
            await using var failed = await state.StartAsync();
        }).Throws<InvalidOperationException>();
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();
        await Assert.That((await File.ReadAllBytesAsync(state.Path(state.Source))).SequenceEqual(source)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(state.Path(state.Neighbor))).SequenceEqual(neighbor)).IsTrue();
        await Assert.That(await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db"))).IsEquivalentTo(catalog);
    }

    [Test]
    public async Task A_missing_branch_source_clears_the_unpublished_targets_intent()
    {
        await using var state = new RecoveryData();
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
        }

        // Reproduce an operator-removed source file after its owning host has closed every connection.
        File.Delete(state.Path(state.Source));
        await using var restarted = await state.StartAsync();
        using var owner = restarted.As(state.User);
        using var branch = await owner.PostAsJsonAsync(Uri(state.Databases + "/" + state.Source + "/branches"), new { name = state.TargetName });
        await Assert.That(branch.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await PendingAsync(restarted)).Documents).IsEmpty();
        await Assert.That(await CatalogAsync(restarted, $"projects/{state.Project}/database_names", state.TargetName)).IsNull();
        var page = await owner.GetFromJsonAsync<JsonElement>(Uri(state.Databases));
        await Assert.That(page.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        using var data = restarted.DataClient(state.ApiKey);
        await state.AssertDocumentAsync(data, "neighbor");
    }

    [Test]
    [Arguments("management", "create")]
    [Arguments("management", "copy")]
    [Arguments("branch", "create")]
    [Arguments("branch", "copy")]
    public async Task Creation_intent_holds_the_destination_until_storage_is_published(string route, string competingOperation)
    {
        await using var state = new RecoveryData();
        IStorageEngine? engine = null;
        var armed = false;
        var intent = new TaskCompletionSource<(TenantContext Storage, Task<bool> Competitor, bool Completed, bool Published)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await state.StartAsync(services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
        {
            if (armed && step == "create.intent")
            {
                // Invoke on this thread: the old unlocked gap lets raw creation publish synchronously here.
                var competing = competingOperation == "create"
                    ? engine!.CreateDatabaseAsync(storage, CancellationToken.None).AsTask()
                    : engine!.CopyDatabaseAsync(storage with { DatabaseId = state.Neighbor }, storage, CancellationToken.None).AsTask();
                intent.SetResult((storage, competing, competing.IsCompleted, File.Exists(state.Path(storage.DatabaseId!))));
                // Leave the void hook without awaiting the competitor, which needs the held creation lock.
            }
        }));
        await state.SeedAsync(api);
        engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        using var owner = api.As(state.User);
        using var data = api.DataClient(state.ApiKey);
        var neighborOnly = EntityIds.New().ToString();
        using (var write = await data.PutAsJsonAsync(
            Uri($"/v1/projects/{state.Project}/databases/neighbor/collections/people/documents/by-key?id={neighborOnly}"),
            new { name = state.Faker.Name.FullName() }))
        {
            write.EnsureSuccessStatusCode();
        }

        armed = true;
        using var response = await owner.PostAsJsonAsync(
            Uri(route == "branch" ? state.Databases + "/" + state.Source + "/branches" : state.Databases), new { name = state.TargetName });
        await Assert.That(intent.Task.IsCompleted).IsTrue();
        var observed = await intent.Task;
        await Assert.That(async () =>
        {
            _ = await observed.Competitor;
        }).Throws<IOException>();
        await Assert.That(observed.Completed).IsFalse();
        await Assert.That(observed.Published).IsFalse();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var target = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await Assert.That(target).IsEqualTo(observed.Storage.DatabaseId);
        using var registered = await owner.GetAsync(Uri(state.Databases + "/" + target));
        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await CatalogAsync(api, $"projects/{state.Project}/database_names", state.TargetName))
            .IsEqualTo(JsonSerializer.Serialize(target));
        await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        await Assert.That(await engine.CreateDatabaseAsync(observed.Storage, CancellationToken.None)).IsFalse();
        if (route == "branch")
        {
            await state.AssertDocumentAsync(data, state.TargetName);
        }
        else
        {
            await state.WriteDocumentAsync(data, state.TargetName);
            await state.AssertDocumentAsync(data, state.TargetName);
        }

        using var absent = await data.GetAsync(
            Uri($"/v1/projects/{state.Project}/databases/{state.TargetName}/collections/people/documents/by-key?id={neighborOnly}"));
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await state.AssertPreservedAsync(api, owner);
    }

    [Test]
    [Arguments("management", "create", false)]
    [Arguments("management", "copy", false)]
    [Arguments("branch", "create", false)]
    [Arguments("branch", "copy", false)]
    [Arguments("management", "create", true)]
    [Arguments("management", "copy", true)]
    [Arguments("branch", "create", true)]
    [Arguments("branch", "copy", true)]
    public async Task A_failed_creation_intent_rejects_queued_storage_until_cleanup(string route, string competingOperation, bool cancel)
    {
        await using var state = new RecoveryData();
        using var cancellation = new CancellationTokenSource();
        IStorageEngine? engine = null;
        var armed = false;
        var intent = new TaskCompletionSource<(TenantContext Storage, Task<bool> Competitor, bool Completed, bool Published)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string target;
        await using (var api = await state.StartAsync(services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
        {
            if (armed && step == "create.intent")
            {
                var competing = competingOperation == "create"
                    ? engine!.CreateDatabaseAsync(storage, CancellationToken.None).AsTask()
                    : engine!.CopyDatabaseAsync(storage with { DatabaseId = state.Neighbor }, storage, CancellationToken.None).AsTask();
                intent.SetResult((storage, competing, competing.IsCompleted, File.Exists(state.Path(storage.DatabaseId!))));
                // Queue under the creation lock; the reservation must survive its release on failure.
                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    throw new InvalidOperationException("interrupted creation intent");
                }
            }
        })))
        {
            await state.SeedAsync(api);
            engine = api.Server.Services.GetRequiredService<IStorageEngine>();
            using var owner = api.As(state.User);
            armed = true;
            if (cancel)
            {
                // Cancel the real service token, avoiding a race with HTTP client disconnection.
                var management = api.Server.Services.GetRequiredService<IManagementService>();
                await Assert.That(async () =>
                {
                    if (route == "branch")
                    {
                        _ = await management.BranchDatabaseAsync(
                            state.User, state.Organization, state.Project, state.Source, state.TargetName, cancellation.Token);
                    }
                    else
                    {
                        _ = await management.CreateDatabaseAsync(
                            state.User, state.Organization, state.Project, state.TargetName, cancellation.Token);
                    }
                }).Throws<OperationCanceledException>();
            }
            else
            {
                using var response = await owner.PostAsJsonAsync(
                    Uri(route == "branch" ? state.Databases + "/" + state.Source + "/branches" : state.Databases), new { name = state.TargetName });
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            }

            await Assert.That(intent.Task.IsCompleted).IsTrue();
            var observed = await intent.Task;
            target = observed.Storage.DatabaseId!;
            await Assert.That(async () =>
            {
                _ = await observed.Competitor;
            }).Throws<IOException>();
            await Assert.That(observed.Completed).IsFalse();
            await Assert.That(observed.Published).IsFalse();
            var pending = await PendingAsync(api);
            await Assert.That(pending.Documents.Count).IsEqualTo(cancel ? 0 : 1);
            if (!cancel)
            {
                await Assert.That(pending.Documents[0].Id).IsEqualTo(target);
                using var document = JsonDocument.Parse(pending.Documents[0].Json);
                await Assert.That(JsonSerializer.Deserialize(document.RootElement, TerraBaseJsonContext.Default.TenantContext))
                    .IsEqualTo(observed.Storage);
            }

            await state.AssertFilesGoneAsync(target);
            if (cancel)
            {
                await Assert.That(await engine.CreateDatabaseAsync(observed.Storage, CancellationToken.None)).IsTrue();
                await Assert.That(await engine.DeleteDatabaseAsync(observed.Storage, CancellationToken.None)).IsTrue();
                await state.AssertFilesGoneAsync(target);
            }
            else
            {
                await Assert.That(async () =>
                {
                    _ = await engine.CreateDatabaseAsync(observed.Storage, CancellationToken.None);
                }).Throws<IOException>();
            }

            await state.AssertUnregisteredAsync(api, owner, target);
            await state.AssertPreservedAsync(api, owner);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            await using var api = await state.StartAsync();
            using var owner = api.As(state.User);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await state.AssertFilesGoneAsync(target);
            await AssertReadyAsync(api);
            await state.AssertUnregisteredAsync(api, owner, target);
            await state.AssertPreservedAsync(api, owner);
        }
    }

    [Test]
    public async Task Startup_checks_each_database_registration_once_and_reports_every_unknown_artifact()
    {
        await using var state = new RecoveryData();
        var target = EntityIds.New().ToString();
        TenantContext storage;
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
            storage = new TenantContext(state.Organization, PlanTier.Hobby, state.Project, target);
            await Assert.That(await api.Server.Services.GetRequiredService<IStorageEngine>()
                .CreateDatabaseAsync(storage, CancellationToken.None)).IsTrue();
        }

        await state.WriteArtifactsAsync(target, sidecars: true);
        var notes = System.IO.Path.Combine(state.Directory, state.Organization, state.Project, "operator-notes.txt");
        await File.WriteAllBytesAsync(notes, state.Faker.Random.Bytes(41));
        var paths = System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(state.Path(target))!)
            .Where(path => System.IO.Path.GetFileName(path).Contains(target, StringComparison.Ordinal) || path == notes).ToArray();
        await Assert.That(paths.Length).IsEqualTo(11);
        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            before[path] = await File.ReadAllBytesAsync(path);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            var lookups = new ConcurrentDictionary<TenantContext, int>();
            var logs = new CapturedLogs();
            await using var api = await state.StartAsync(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
                {
                    if (step == "audit.registration")
                    {
                        lookups.AddOrUpdate(database, 1, (_, count) => count + 1);
                    }
                });
            });
            await Assert.That(lookups.GetValueOrDefault(storage)).IsEqualTo(1);
            await Assert.That(lookups.Count).IsEqualTo(3);
            await Assert.That(lookups.Values.All(count => count == 1)).IsTrue();
            foreach (var (path, bytes) in before)
            {
                await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)).IsTrue();
                await AssertFileLoggedAsync(api, logs, path);
            }

            await AssertUnknownCountAsync(api, paths.Length);
            await AssertReadyAsync(api);
            using var owner = api.As(state.User);
            await state.AssertUnregisteredAsync(api, owner, target);
            await state.AssertPreservedAsync(api, owner);
        }
    }

    [Test]
    public async Task Engine_registration_without_management_can_create_storage_preserved_by_server_restart()
    {
        await using var state = new RecoveryData();
        await using (var api = await state.StartAsync())
        {
            await state.SeedAsync(api);
        }

        var target = EntityIds.New().ToString();
        var options = new TerraBaseOptions { DataDirectory = state.Directory, MemoryBudget = 64 * 1024 * 1024, StorageThreads = 1, StorageQueue = 16 };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddLogging();
        services.AddTerraBaseEngine(options);
        services.AddSingleton<INativeTemporaryStorage, TestNativeTemporaryStorage>();
        services.AddKeyedSingleton<IMasterKey, TestMasterKey>(EngineExtensions.DatabaseMasterKey);
        await using (var provider = services.BuildServiceProvider())
        {
            var engine = provider.GetRequiredService<IStorageEngine>();
            _ = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
            await Assert.That(await engine.CreateDatabaseAsync(
                new TenantContext(state.Organization, PlanTier.Hobby, state.Project, target), CancellationToken.None)).IsTrue();
        }

        var path = state.Path(target);
        var bytes = await File.ReadAllBytesAsync(path);
        var logs = new CapturedLogs();
        await using var restarted = await state.StartAsync(registered => registered.AddSingleton<ILoggerProvider>(logs));
        await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)).IsTrue();
        await AssertFileLoggedAsync(restarted, logs, path);
        await AssertFileLoggedAsync(restarted, logs, path + ".key");
        await AssertUnknownCountAsync(restarted, 2);
        await AssertReadyAsync(restarted);
        await Assert.That((await PendingAsync(restarted)).Documents).IsEmpty();
        using var owner = restarted.As(state.User);
        await state.AssertUnregisteredAsync(restarted, owner, target);
        await state.AssertPreservedAsync(restarted, owner);
    }

    [Test]
    [Arguments("database")]
    [Arguments("sidecar")]
    [Arguments("temporary")]
    [Arguments("database-uppercase-extension")]
    [Arguments("sidecar-uppercase-suffix")]
    [Arguments("temporary-uppercase")]
    [Arguments("database-directory")]
    [Arguments("sidecar-directory")]
    [Arguments("temporary-directory")]
    [Arguments("temporary-uppercase-directory")]
    public async Task A_generated_id_collision_does_not_authorize_cleanup_of_unknown_files(string artifact)
    {
        await using var state = new RecoveryData();
        var isDirectory = artifact.EndsWith("-directory", StringComparison.Ordinal);
        var armed = false;
        var target = string.Empty;
        var path = string.Empty;
        var bytes = state.Faker.Random.Bytes(89);
        await using (var api = await state.StartAsync(services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
        {
            if (armed && step == "create.prepare")
            {
                target = storage.DatabaseId!;
                path = artifact switch
                {
                    "sidecar" or "sidecar-directory" => state.Path(target) + "-wal",
                    "database-uppercase-extension" => System.IO.Path.ChangeExtension(state.Path(target), ".DB"),
                    "sidecar-uppercase-suffix" => state.Path(target) + "-WAL",
                    "temporary-uppercase" or "temporary-uppercase-directory" => System.IO.Path.Combine(state.Directory, storage.OrganizationId, storage.ProjectId!,
                        $".{target}.{EntityIds.New()}.creating".ToUpperInvariant()),
                    "temporary" or "temporary-directory" => System.IO.Path.Combine(state.Directory, storage.OrganizationId, storage.ProjectId!,
                        $".{target}.{EntityIds.New()}.creating"),
                    _ => state.Path(target),
                };
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (isDirectory)
                {
                    System.IO.Directory.CreateDirectory(path);
                }
                else
                {
                    File.WriteAllBytes(path, bytes);
                }
            }
        })))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            armed = true;
            using var create = await owner.PostAsJsonAsync(Uri(state.Databases), new { name = state.TargetName });
            await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(target).IsNotEqualTo(string.Empty);
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await AssertArtifactPreservedAsync();
            await state.AssertUnregisteredAsync(api, owner, target);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            var logs = new CapturedLogs();
            await using var api = await state.StartAsync(services => services.AddSingleton<ILoggerProvider>(logs));
            await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
            await AssertArtifactPreservedAsync();
            await AssertReadyAsync(api);
            await AssertFileLoggedAsync(api, logs, path);
            await AssertUnknownCountAsync(api, 1);
            using var owner = api.As(state.User);
            await state.AssertUnregisteredAsync(api, owner, target);
            await state.AssertPreservedAsync(api, owner);
        }

        async Task AssertArtifactPreservedAsync()
        {
            if (isDirectory)
            {
                await Assert.That(System.IO.Directory.Exists(path)).IsTrue();
                await Assert.That(new DirectoryInfo(path).LinkTarget).IsNull();
                await Assert.That(System.IO.Directory.EnumerateFileSystemEntries(path)).IsEmpty();
            }
            else
            {
                await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)).IsTrue();
            }
        }
    }

    [Test]
    [Arguments("management", "organization")]
    [Arguments("management", "project")]
    [Arguments("branch", "organization")]
    [Arguments("branch", "project")]
    [Arguments("data", "organization")]
    [Arguments("data", "project")]
    [Arguments("delete", "organization")]
    [Arguments("delete", "project")]
    public async Task A_parent_link_installed_during_a_lifecycle_callback_refuses_file_work_and_replay(string route, string parent)
    {
        await using var state = new RecoveryData();
        await using var external = new RecoveryData();
        await using var backup = new RecoveryData();
        var original = System.IO.Path.Combine(backup.Directory, "original-parent");
        var link = string.Empty;
        var armed = false;
        TenantContext? captured = null;
        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string[] externalEntries = [];
        await using (var api = await state.StartAsync(services => services
            .AddSingleton<TimeProvider>(new FakeTimeProvider(TimeProvider.System.GetUtcNow()))
            .AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
        {
            if (armed && step == (route == "delete" ? "delete.main_file" : "create.intent"))
            {
                armed = false;
                captured = storage;
                state.TargetPath = state.Path(storage.DatabaseId!);
                link = parent == "organization"
                    ? System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(state.TargetPath)!)!
                    : System.IO.Path.GetDirectoryName(state.TargetPath)!;
                var directory = parent == "organization"
                    ? System.IO.Path.Combine(external.Directory, storage.ProjectId!)
                    : external.Directory;
                System.IO.Directory.CreateDirectory(directory);
                var target = System.IO.Path.Combine(directory, storage.DatabaseId + ".db");
                var temporary = System.IO.Path.Combine(directory, $".{storage.DatabaseId}.{EntityIds.New()}.creating");
                // An existing mapped main file would mask the initialization gap on create/copy.
                var main = route == "delete" ? target : System.IO.Path.Combine(directory, "operator-archive", storage.DatabaseId + ".db");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(main)!);
                File.WriteAllBytes(main, state.Faker.Random.Bytes(73));
                foreach (var suffix in state.Sidecars.Prepend(string.Empty))
                {
                    File.WriteAllBytes(temporary + suffix, state.Faker.Random.Bytes(73));
                    if (suffix.Length != 0)
                    {
                        File.WriteAllBytes(target + suffix, state.Faker.Random.Bytes(73));
                    }
                }

                // Cached source/neighbor stores must not unlink these paths during shutdown.
                foreach (var id in new[] { state.Source, state.Neighbor })
                {
                    foreach (var suffix in state.Sidecars.Prepend(string.Empty))
                    {
                        File.WriteAllBytes(System.IO.Path.Combine(directory, id + ".db") + suffix, state.Faker.Random.Bytes(73));
                    }
                }

                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(directory, "operator-empty"));
                externalEntries = System.IO.Directory.GetFileSystemEntries(external.Directory, "*", SearchOption.AllDirectories);
                foreach (var path in System.IO.Directory.GetFiles(external.Directory, "*", SearchOption.AllDirectories))
                {
                    before[path] = File.ReadAllBytes(path);
                }

                ReplaceParent();
                // Return normally: the engine must revalidate the parent after this callback.
            }
        })))
        {
            try
            {
                await state.SeedAsync(api);
                using var owner = api.As(state.User);
                var deleting = route == "delete"
                    ? await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner)
                    : null;
                if (deleting is not null)
                {
                    await state.WriteArtifactsAsync(deleting, sidecars: false);
                }

                using var data = api.DataClient(state.ApiKey);
                armed = true;
                using var response = route switch
                {
                    "delete" => await owner.DeleteAsync(Uri(state.Databases + "/" + deleting)),
                    "data" => await data.PutAsync(Uri($"/v1/projects/{state.Project}/databases/{state.TargetName}"), null),
                    _ => await owner.PostAsJsonAsync(
                        Uri(route == "branch" ? state.Databases + "/" + state.Source + "/branches" : state.Databases), new { name = state.TargetName }),
                };
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
                await Assert.That(captured).IsNotNull();
                var storage = captured!;
                var target = storage.DatabaseId!;
                await Assert.That(storage.OrganizationId).IsEqualTo(state.Organization);
                await Assert.That(storage.ProjectId).IsEqualTo(state.Project);
                var pending = await PendingAsync(api);
                await Assert.That(pending.Documents.Count).IsEqualTo(1);
                await Assert.That(pending.Documents[0].Id).IsEqualTo(target);
                using var intent = JsonDocument.Parse(pending.Documents[0].Json);
                await Assert.That(JsonSerializer.Deserialize(intent.RootElement, TerraBaseJsonContext.Default.TenantContext)).IsEqualTo(storage);
                // Inspect only the safe system catalog while the tenant directory is linked.
                await Assert.That(await CatalogAsync(api, $"projects/{state.Project}/databases", target)).IsNull();
                await Assert.That(await CatalogAsync(api, $"projects/{state.Project}/database_names", state.TargetName)).IsNull();
                await AssertExternalPreservedAsync();
            }
            finally
            {
                // Restore before disposing cached Turso stores, whose original paths include this parent.
                RestoreParent();
            }
        }

        await Assert.That(captured).IsNotNull();
        var database = captured!.DatabaseId!;
        await AssertExternalPreservedAsync(linked: false);
        await state.WriteArtifactsAsync(database, sidecars: true);
        try
        {
            ReplaceParent();
            // The owning host is stopped; replay must refuse the link and retain cleanup ownership.
            await Assert.That(async () =>
            {
                await using var failed = await state.StartAsync();
            }).Throws<IOException>();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();
            await Assert.That(await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db")))
                .Contains(line => line.StartsWith("pending_database_cleanup|" + database + "|", StringComparison.Ordinal));
            await AssertExternalPreservedAsync();
        }
        finally
        {
            RestoreParent();
        }

        for (var restart = 0; restart < 2; restart++)
        {
            await using var recovered = await state.StartAsync();
            await state.AssertFilesGoneAsync(database);
            await Assert.That((await PendingAsync(recovered)).Documents).IsEmpty();
            await AssertExternalPreservedAsync(linked: false);
            await AssertReadyAsync(recovered);
            using var owner = recovered.As(state.User);
            await state.AssertUnregisteredAsync(recovered, owner, database);
            await state.AssertPreservedAsync(recovered, owner);
        }

        void ReplaceParent()
        {
            System.IO.Directory.Move(link, original);
            System.IO.Directory.CreateSymbolicLink(link, external.Directory);
        }

        void RestoreParent()
        {
            if (link.Length != 0 && new DirectoryInfo(link).LinkTarget == external.Directory)
            {
                // Remove only our link, never recurse into the external directory.
                System.IO.Directory.Delete(link);
            }

            if (System.IO.Directory.Exists(original))
            {
                System.IO.Directory.Move(original, link);
            }
        }

        async Task AssertExternalPreservedAsync(bool linked = true)
        {
            await Assert.That(new DirectoryInfo(link).LinkTarget).IsEqualTo(linked ? external.Directory : null);
            await Assert.That(System.IO.Directory.GetFileSystemEntries(external.Directory, "*", SearchOption.AllDirectories))
                .IsEquivalentTo(externalEntries);
            foreach (var (path, bytes) in before)
            {
                await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes)).IsTrue();
            }
        }
    }

    [Test]
    [Arguments("create", "organization")]
    [Arguments("create", "project")]
    [Arguments("delete", "organization")]
    [Arguments("delete", "project")]
    public async Task An_intent_with_an_absent_tenant_directory_is_finalized_on_restart(string operation, string parent)
    {
        await using var state = new RecoveryData();
        var armed = false;
        TenantContext? captured = null;
        var organization = string.Empty;
        var project = string.Empty;
        var organizationDirectory = string.Empty;
        var projectDirectory = string.Empty;
        var note = string.Empty;
        var bytes = state.Faker.Random.Bytes(37);
        // Preserve the injected crash state; background cleanup resumes on the real-clock restart hosts.
        await using (var api = await state.StartAsync(services => services
            .AddSingleton<TimeProvider>(new FakeTimeProvider(TimeProvider.System.GetUtcNow()))
            .AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
        {
            if (armed && step == (operation == "create" ? "create.intent" : "cleanup.files"))
            {
                captured = storage;
                throw new InvalidOperationException("interrupted lifecycle with an absent tenant directory");
            }
        })))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            // Use a separate tenant so its last database can disappear without removing the seeded data.
            organization = await api.CreateOrganizationAsync("Recovery " + state.Faker.Company.CompanyName(), owner);
            project = await api.CreateProjectAsync(organization, "Project " + state.Faker.Random.AlphaNumeric(8), owner);
            organizationDirectory = System.IO.Path.Combine(state.Directory, organization);
            projectDirectory = System.IO.Path.Combine(organizationDirectory, project);
            if (parent == "project")
            {
                System.IO.Directory.CreateDirectory(organizationDirectory);
                note = System.IO.Path.Combine(organizationDirectory, "operator-notes.txt");
                await File.WriteAllBytesAsync(note, bytes);
            }

            var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
            var deleting = operation == "delete"
                ? await api.CreateDatabaseAsync(organization, project, state.TargetName, owner)
                : null;
            armed = true;
            using var response = operation == "delete"
                ? await owner.DeleteAsync(Uri(databases + "/" + deleting))
                : await owner.PostAsJsonAsync(Uri(databases), new { name = state.TargetName });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(captured).IsNotNull();
            var storage = captured!;
            await Assert.That(storage.OrganizationId).IsEqualTo(organization);
            await Assert.That(storage.ProjectId).IsEqualTo(project);
            if (deleting is not null)
            {
                await Assert.That(storage.DatabaseId).IsEqualTo(deleting);
            }

            var pending = await PendingAsync(api);
            await Assert.That(pending.Documents.Count).IsEqualTo(1);
            await Assert.That(pending.Documents[0].Id).IsEqualTo(storage.DatabaseId);
            using var intent = JsonDocument.Parse(pending.Documents[0].Json);
            await Assert.That(JsonSerializer.Deserialize(intent.RootElement, TerraBaseJsonContext.Default.TenantContext)).IsEqualTo(storage);
            await Assert.That(await CatalogAsync(api, $"projects/{project}/databases", storage.DatabaseId!)).IsNull();
            await Assert.That(await CatalogAsync(api, $"projects/{project}/database_names", state.TargetName)).IsNull();
            await AssertAbsentDirectoryAsync();
        }

        var database = captured!.DatabaseId!;
        // The failed host must release its stores before inspecting the durable intent and missing parent.
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(state.Directory)).IsFalse();
        await AssertAbsentDirectoryAsync();
        await Assert.That(await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db")))
            .Contains(line => line.StartsWith("pending_database_cleanup|" + database + "|", StringComparison.Ordinal));
        for (var restart = 0; restart < 2; restart++)
        {
            await using var recovered = await state.StartAsync();
            // Missing parents already mean cleanup is complete; replay must not recreate them.
            await Assert.That((await PendingAsync(recovered)).Documents).IsEmpty();
            await AssertAbsentDirectoryAsync();
            var path = System.IO.Path.Combine(projectDirectory, database + ".db");
            foreach (var suffix in state.Sidecars.Prepend(string.Empty))
            {
                await Assert.That(File.Exists(path + suffix)).IsFalse();
            }

            await Assert.That(await CatalogAsync(recovered, "organizations", organization)).IsNotNull();
            await Assert.That(await CatalogAsync(recovered, $"organizations/{organization}/projects", project)).IsNotNull();
            await Assert.That(await CatalogAsync(recovered, $"projects/{project}/databases", database)).IsNull();
            await Assert.That(await CatalogAsync(recovered, $"projects/{project}/database_names", state.TargetName)).IsNull();
            await AssertReadyAsync(recovered);
            using var owner = recovered.As(state.User);
            using var missing = await owner.GetAsync(Uri(
                $"/v1/management/organizations/{organization}/projects/{project}/databases/{database}"));
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await state.AssertPreservedAsync(recovered, owner);
            await AssertAbsentDirectoryAsync();
            if (restart == 1)
            {
                // Reuse the same catalog parents and database name after two idempotent replays.
                var replacement = await recovered.CreateDatabaseAsync(organization, project, state.TargetName, owner);
                await Assert.That(replacement).IsNotEqualTo(database);
                await Assert.That(File.Exists(System.IO.Path.Combine(projectDirectory, replacement + ".db"))).IsTrue();
                await Assert.That(await CatalogAsync(recovered, $"projects/{project}/database_names", state.TargetName))
                    .IsEqualTo(JsonSerializer.Serialize(replacement));
            }
        }

        async Task AssertAbsentDirectoryAsync()
        {
            await Assert.That(System.IO.Directory.Exists(projectDirectory)).IsFalse();
            await Assert.That(System.IO.Directory.Exists(organizationDirectory)).IsEqualTo(parent == "project");
            if (note.Length != 0)
            {
                await Assert.That((await File.ReadAllBytesAsync(note)).SequenceEqual(bytes)).IsTrue();
            }
        }
    }

    [Test]
    public async Task Cancellation_before_delete_intent_commit_preserves_data_and_allows_immediate_retry()
    {
        await using var state = new RecoveryData();
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        var reached = false;
        await using var api = await state.StartAsync(services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            if (armed && step == "delete.recording")
            {
                reached = true;
                cancellation.Cancel();
            }
        }));
        await state.SeedAsync(api);
        using var owner = api.As(state.User);
        var target = await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner);
        using var data = api.DataClient(state.ApiKey);
        await state.WriteDocumentAsync(data, state.TargetName);
        armed = true;
        var management = api.Server.Services.GetRequiredService<IManagementService>();
        await Assert.That(async () =>
        {
            _ = await management.DeleteDatabaseAsync(state.User, state.Organization, state.Project, target, cancellation.Token);
        }).Throws<OperationCanceledException>();
        await Assert.That(reached).IsTrue();
        await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        await Assert.That(File.Exists(state.Path(target))).IsTrue();
        using (var registered = await owner.GetAsync(Uri(state.Databases + "/" + target)))
        {
            await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await state.AssertDocumentAsync(data, state.TargetName);
        armed = false;
        using var deleted = await owner.DeleteAsync(Uri(state.Databases + "/" + target));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        // The committed deletion keeps its intent until erasure is verified (no native handle left, which a background
        // pass may still hold for a moment); convergence then clears it.
        await TerraBaseTestServer.EventuallyAsync(async _ => (await PendingAsync(api)).Documents.Count == 0, timeout: TimeSpan.FromSeconds(30));
        await state.AssertFilesGoneAsync(target);
        await state.AssertUnregisteredAsync(api, owner, target);
        await state.AssertPreservedAsync(api, owner);
    }

    [Test]
    [Arguments("management")]
    [Arguments("forget")]
    public async Task A_catalog_change_after_delete_resolution_is_unavailable_and_preserves_the_database(string route)
    {
        await using var state = new RecoveryData();
        var hook = new UsageCommitHook();
        var reached = new TaskCompletionSource<UsageCommitHook.Gate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        await using var api = await state.StartAsync(services =>
        {
            hook.Install(services);
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (armed && step == "delete.recording")
                {
                    armed = false;
                    reached.TrySetResult(hook.PauseWrite());
                }
            });
        });
        await state.SeedAsync(api);
        using var owner = api.As(state.User);
        var target = await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner);
        using var data = api.DataClient(state.ApiKey);
        await state.WriteDocumentAsync(data, state.TargetName);
        var storage = new TenantContext(state.Organization, PlanTier.Hobby, state.Project, target);
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var tenants = api.Server.Services.GetRequiredService<ITenantDirectory>();
        if (route == "forget")
        {
            // WAN deletion forgets a local copy whose home region is elsewhere.
            await using var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None);
            var document = await transaction.GetAsync($"projects/{state.Project}/databases", target, CancellationToken.None);
            var database = EntityPayload.Read(document!, TerraBaseJsonContext.Default.ManagedDatabase)!;
            var localRegion = api.Server.Services.GetRequiredService<TerraBaseOptions>().Region;
            await transaction.PutAsync($"projects/{state.Project}/databases", target,
                EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(database with { HomeRegion = "elsewhere", ReplicaRegions = [localRegion] },
                    TerraBaseJsonContext.Default.ManagedDatabase), target, nested: true), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        armed = true;
        var deleting = route == "management" ? owner.DeleteAsync(Uri(state.Databases + "/" + target)) : null;
        var forgetting = route == "forget" ? tenants.ForgetDatabaseAsync(storage, CancellationToken.None).AsTask() : null;
        using var gate = await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        byte[] supportedVersion;
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            supportedVersion = (await transaction.GetAsync("catalog", "version", CancellationToken.None))!.Json.ToArray();
            await transaction.PutAsync("catalog", "version", Encoding.UTF8.GetBytes("\"99\""), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var catalog = await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db"));
        var bytes = await File.ReadAllBytesAsync(state.Path(target));
        gate.Dispose();
        if (deleting is not null)
        {
            using var refused = await deleting.WaitAsync(TimeSpan.FromSeconds(10));
            await ErrorAssert.ErrorAsync(refused, HttpStatusCode.ServiceUnavailable, "unavailable",
                "the management catalog has an old or unsupported format; discard the affected data");
        }
        else
        {
            await Assert.That(await forgetting!.WaitAsync(TimeSpan.FromSeconds(10)) is Unavailable).IsTrue();
        }

        await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        await Assert.That((await File.ReadAllBytesAsync(state.Path(target))).SequenceEqual(bytes)).IsTrue();
        await Assert.That(await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db"))).IsEquivalentTo(catalog);
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.PutAsync("catalog", "version", supportedVersion, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await state.AssertDocumentAsync(data, state.TargetName);
        if (route == "management")
        {
            using var deleted = await owner.DeleteAsync(Uri(state.Databases + "/" + target));
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
        else
        {
            await Assert.That(await tenants.ForgetDatabaseAsync(storage, CancellationToken.None) is DatabaseForgotten { Forgotten: true }).IsTrue();
        }

        await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        await state.AssertFilesGoneAsync(target);
        await state.AssertUnregisteredAsync(api, owner, target);
        await state.AssertPreservedAsync(api, owner);
    }

    [Test]
    public async Task A_catalog_refusal_after_file_publication_preserves_creation_ownership_until_recovery()
    {
        await using var state = new RecoveryData();
        var hook = new UsageCommitHook();
        var reached = new TaskCompletionSource<(TenantContext Storage, UsageCommitHook.Gate Gate)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        string target;
        string intent;
        byte[] supportedVersion;
        byte[] projectJson;
        var systemPath = System.IO.Path.Combine(state.Directory, "_system.db");
        List<string> catalog;
        await using (var api = await state.StartAsync(services =>
        {
            hook.Install(services);
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
            {
                if (armed && step == "create.files")
                {
                    armed = false;
                    reached.TrySetResult((storage, hook.PauseWrite()));
                }
            });
        }))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            armed = true;
            var creating = owner.PostAsJsonAsync(Uri(state.Databases), new { name = state.TargetName });
            var stopped = await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var gate = stopped.Gate;
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            target = stopped.Storage.DatabaseId!;
            var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                supportedVersion = (await transaction.GetAsync("catalog", "version", CancellationToken.None))!.Json.ToArray();
                projectJson = (await transaction.GetAsync($"organizations/{state.Organization}/projects", state.Project, CancellationToken.None))!.Json.ToArray();
                await transaction.PutAsync("catalog", "version", Encoding.UTF8.GetBytes("\"99\""), CancellationToken.None);
                // Registration must refuse before trying to deserialize an unknown catalog representation.
                await transaction.PutAsync($"organizations/{state.Organization}/projects", state.Project,
                    Encoding.UTF8.GetBytes("{\"createdAt\":{\"unixNanos\":\"1\"}}"), CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
            }

            var pending = await PendingAsync(api);
            await Assert.That(pending.Documents.Count).IsEqualTo(1);
            await Assert.That(pending.Documents[0].Id).IsEqualTo(target);
            intent = Encoding.UTF8.GetString(pending.Documents[0].Json.Span);
            var bytes = await File.ReadAllBytesAsync(state.Path(target));
            catalog = await DocumentFiles.ReadAsync(systemPath);
            gate.Dispose();
            using var refused = await creating.WaitAsync(TimeSpan.FromSeconds(10));
            await ErrorAssert.ErrorAsync(refused, HttpStatusCode.ServiceUnavailable, "unavailable",
                "the management catalog has an old or unsupported format; discard the affected data");
            await Assert.That(await CatalogAsync(api, "pending_database_cleanup", target)).IsEqualTo(intent);
            await Assert.That(await CatalogAsync(api, $"projects/{state.Project}/databases", target)).IsNull();
            await Assert.That((await File.ReadAllBytesAsync(state.Path(target))).SequenceEqual(bytes)).IsTrue();
            await Assert.That(await DocumentFiles.ReadAsync(systemPath)).IsEquivalentTo(catalog);
        }

        var preserved = await File.ReadAllBytesAsync(state.Path(target));
        await using (var unavailable = await state.StartAsync())
        {
            using var owner = unavailable.As(state.User);
            await ErrorAssert.ErrorAsync(owner.GetAsync(Uri(state.Databases)), HttpStatusCode.ServiceUnavailable, "unavailable",
                "the management catalog has an old or unsupported format; discard the affected data");
            await Assert.That(await CatalogAsync(unavailable, "pending_database_cleanup", target)).IsEqualTo(intent);
            await Assert.That((await File.ReadAllBytesAsync(state.Path(target))).SequenceEqual(preserved)).IsTrue();
            await Assert.That(await DocumentFiles.ReadAsync(systemPath)).IsEquivalentTo(catalog);
            await AssertReadyAsync(unavailable);
        }

        await DocumentFiles.UpdateAsync(systemPath,
            ("catalog", "version", Encoding.UTF8.GetString(supportedVersion)),
            ($"organizations/{state.Organization}/projects", state.Project, Encoding.UTF8.GetString(projectJson)));
        await using var recovered = await state.StartAsync();
        using var recoveredOwner = recovered.As(state.User);
        await Assert.That((await PendingAsync(recovered)).Documents).IsEmpty();
        await state.AssertFilesGoneAsync(target);
        await state.AssertUnregisteredAsync(recovered, recoveredOwner, target);
        await state.AssertPreservedAsync(recovered, recoveredOwner);
        await AssertReadyAsync(recovered);
        await recovered.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, recoveredOwner);
    }

    [Test]
    [Arguments("management")]
    [Arguments("commit")]
    public async Task A_catalog_refusal_before_creation_intent_leaves_no_owned_files_or_catalog_changes(string route)
    {
        await using var state = new RecoveryData();
        var hook = new UsageCommitHook();
        var reached = new TaskCompletionSource<(TenantContext Storage, UsageCommitHook.Gate Gate)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        await using var api = await state.StartAsync(services =>
        {
            hook.Install(services);
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
            {
                if (armed && step == "create.prepare")
                {
                    armed = false;
                    reached.TrySetResult((storage, hook.PauseWrite()));
                }
            });
        });
        await state.SeedAsync(api);
        using var owner = api.As(state.User);
        armed = true;
        var creating = CreateAsync();
        var stopped = await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var gate = stopped.Gate;
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var target = stopped.Storage.DatabaseId!;
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        byte[] supportedVersion;
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            supportedVersion = (await transaction.GetAsync("catalog", "version", CancellationToken.None))!.Json.ToArray();
            await transaction.PutAsync("catalog", "version", Encoding.UTF8.GetBytes("\"99\""), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var catalog = await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db"));
        await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        await state.AssertFilesGoneAsync(target);
        gate.Dispose();
        using var refused = await creating.WaitAsync(TimeSpan.FromSeconds(10));
        await ErrorAssert.ErrorAsync(refused, HttpStatusCode.ServiceUnavailable, "unavailable",
            "the management catalog has an old or unsupported format; discard the affected data");
        await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        await state.AssertFilesGoneAsync(target);
        await Assert.That(await DocumentFiles.ReadAsync(System.IO.Path.Combine(state.Directory, "_system.db"))).IsEquivalentTo(catalog);
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.PutAsync("catalog", "version", supportedVersion, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        using var retried = await CreateAsync();
        await Assert.That(retried.StatusCode).IsEqualTo(route == "commit" ? HttpStatusCode.OK : HttpStatusCode.Created);
        var body = await retried.Content.ReadFromJsonAsync<JsonElement>();
        var replacement = (route == "commit" ? body.GetProperty("results")[0].GetProperty("value") : body).GetProperty("id").GetString()!;
        await Assert.That(File.Exists(state.Path(replacement))).IsTrue();
        await Assert.That((await PendingAsync(api)).Documents).IsEmpty();
        using var data = api.DataClient(state.ApiKey);
        await state.WriteDocumentAsync(data, state.TargetName);
        await state.AssertUnregisteredAsync(api, owner, target, replacement);
        await state.AssertPreservedAsync(api, owner);

        Task<HttpResponseMessage> CreateAsync()
        {
            return route == "commit"
            ? owner.PostAsJsonAsync(Uri("/v1/management/commits"), new
            {
                id = state.Organization,
                ops = new[] { new { op = "create_database", organization = state.Organization, project = state.Project, name = state.TargetName } },
            })
            : owner.PostAsJsonAsync(Uri(state.Databases), new { name = state.TargetName });
        }
    }

    [Test]
    public async Task A_catalog_refusal_while_clearing_deletion_retains_the_intent_until_supported_restart()
    {
        await using var state = new RecoveryData();
        var hook = new UsageCommitHook();
        var reached = new TaskCompletionSource<UsageCommitHook.Gate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        string target;
        string intent;
        byte[] supportedVersion;
        List<string> catalog;
        var systemPath = System.IO.Path.Combine(state.Directory, "_system.db");
        await using (var api = await state.StartAsync(services =>
        {
            hook.Install(services);
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (armed && step == "cleanup.clearing")
                {
                    armed = false;
                    reached.TrySetResult(hook.PauseWrite());
                }
            });
        }))
        {
            await state.SeedAsync(api);
            using var owner = api.As(state.User);
            target = await api.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, owner);
            using var data = api.DataClient(state.ApiKey);
            await state.WriteDocumentAsync(data, state.TargetName);
            armed = true;
            var deleting = owner.DeleteAsync(Uri(state.Databases + "/" + target));
            using var gate = await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await state.AssertFilesGoneAsync(target);
            var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                supportedVersion = (await transaction.GetAsync("catalog", "version", CancellationToken.None))!.Json.ToArray();
                await transaction.PutAsync("catalog", "version", Encoding.UTF8.GetBytes("\"99\""), CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
            }

            var pending = await PendingAsync(api);
            await Assert.That(pending.Documents.Count).IsEqualTo(1);
            await Assert.That(pending.Documents[0].Id).IsEqualTo(target);
            intent = Encoding.UTF8.GetString(pending.Documents[0].Json.Span);
            catalog = await DocumentFiles.ReadAsync(systemPath);
            gate.Dispose();
            using var refused = await deleting.WaitAsync(TimeSpan.FromSeconds(10));
            await ErrorAssert.ErrorAsync(refused, HttpStatusCode.ServiceUnavailable, "unavailable",
                "the management catalog has an old or unsupported format; discard the affected data");
            await Assert.That(await CatalogAsync(api, "pending_database_cleanup", target)).IsEqualTo(intent);
            await Assert.That(await DocumentFiles.ReadAsync(systemPath)).IsEquivalentTo(catalog);
            await state.AssertFilesGoneAsync(target);
        }

        await using (var unavailable = await state.StartAsync())
        {
            using var owner = unavailable.As(state.User);
            await ErrorAssert.ErrorAsync(owner.GetAsync(Uri(state.Databases)), HttpStatusCode.ServiceUnavailable, "unavailable",
                "the management catalog has an old or unsupported format; discard the affected data");
            await Assert.That(await CatalogAsync(unavailable, "pending_database_cleanup", target)).IsEqualTo(intent);
            await Assert.That(await DocumentFiles.ReadAsync(systemPath)).IsEquivalentTo(catalog);
            await state.AssertFilesGoneAsync(target);
            await AssertReadyAsync(unavailable);
        }

        await DocumentFiles.UpdateAsync(systemPath, ("catalog", "version", Encoding.UTF8.GetString(supportedVersion)));
        await using var recovered = await state.StartAsync();
        using var recoveredOwner = recovered.As(state.User);
        await Assert.That((await PendingAsync(recovered)).Documents).IsEmpty();
        await state.AssertFilesGoneAsync(target);
        await state.AssertUnregisteredAsync(recovered, recoveredOwner, target);
        await state.AssertPreservedAsync(recovered, recoveredOwner);
        await AssertReadyAsync(recovered);
        await recovered.CreateDatabaseAsync(state.Organization, state.Project, state.TargetName, recoveredOwner);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    private static async Task AssertReadyAsync(ManagementApi api)
    {
        using var health = await api.Server.Client.GetAsync(Uri("/healthz"));
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var ready = await api.Server.Client.GetAsync(Uri("/readyz"), token);
            if (ready.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return false;
            }

            await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return true;
        });
    }

    private static async Task AssertUnknownCountAsync(ManagementApi api, int expected)
    {
        var metrics = await api.Server.Client.GetStringAsync(Uri("/metrics"));
        var samples = metrics.Split('\n').Where(line => line.StartsWith("terrabase_storage_unknown_files{", StringComparison.Ordinal)
            || line.StartsWith("terrabase_storage_unknown_files ", StringComparison.Ordinal)).ToArray();
        await Assert.That(samples.Length).IsEqualTo(1);
        var count = samples[0];
        await Assert.That(double.Parse(count.Split(' ')[1], CultureInfo.InvariantCulture)).IsEqualTo((double)expected);
    }

    private static async Task<StoredDocument> UnregisterSourceWithCleanupIntentAsync(RecoveryData state)
    {
        // Keep background convergence from consuming the fixture before the startup under test.
        await using var api = await state.StartAsync(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow)));
        await state.SeedAsync(api);
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None);
        var registration = (await transaction.GetAsync($"projects/{state.Project}/databases", state.Source, CancellationToken.None))!;
        var storage = new TenantContext(state.Organization, PlanTier.Hobby, state.Project, state.Source);
        await transaction.PutAsync("pending_database_cleanup", state.Source,
            JsonSerializer.SerializeToUtf8Bytes(storage, TerraBaseJsonContext.Default.TenantContext), CancellationToken.None);
        await transaction.DeleteAsync($"projects/{state.Project}/databases", state.Source, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return registration;
    }

    private static async Task<DocumentPage> PendingAsync(ManagementApi api)
    {
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        return await transaction.ListAsync("pending_database_cleanup", null, 100, 1024 * 1024, CancellationToken.None);
    }

    private static async Task<string?> CatalogAsync(ManagementApi api, string collection, string id)
    {
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        var document = await transaction.GetAsync(collection, id, CancellationToken.None);
        return document is null ? null : Encoding.UTF8.GetString(document.Json.Span);
    }

    private sealed class HeldRecoveryStartup(TaskCompletionSource entered, TaskCompletionSource release, CancellationToken testToken) : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(testToken);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecoveryData : IAsyncDisposable
    {
        private readonly List<ManagementApi> _started = [];
        private readonly List<Task> _failedStarts = [];
        private readonly CancellationToken _cancellationToken;
        private readonly Lazy<Task> _cleanup;

        public Faker Faker { get; } = new();
        public IReadOnlyList<string> Sidecars { get; } = ["-wal", "-shm", "-journal", ".key"];
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("terrabase-recovery-").FullName;
        public string User { get; }
        public string Organization { get; private set; } = string.Empty;
        public string Project { get; private set; } = string.Empty;
        public string Source { get; private set; } = string.Empty;
        public string Neighbor { get; private set; } = string.Empty;
        public string ApiKey { get; private set; } = string.Empty;
        public string TargetName { get; }
        public string TargetPath { get; set; } = string.Empty;
        private string DocumentId { get; }
        private string Person { get; }

        public RecoveryData(CancellationToken? cancellationToken = null)
        {
            _cancellationToken = cancellationToken ?? TestContext.Current!.Execution.CancellationToken;
            _cleanup = new(CleanupAsync);
            User = EntityIds.New().ToString();
            TargetName = "target_" + Faker.Random.AlphaNumeric(8).ToLowerInvariant();
            DocumentId = EntityIds.New().ToString();
            Person = Faker.Name.FullName();
        }

        public string Databases => $"/v1/management/organizations/{Organization}/projects/{Project}/databases";

        public string Path(string database) => System.IO.Path.Combine(Directory, Organization, Project, database + ".db");

        public async Task<ManagementApi> StartAsync(Action<IServiceCollection>? services = null)
        {
            var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = Directory, services,
                cancellationToken: _cancellationToken, failedStartCleanup: _failedStarts.Add);
            _started.Add(api);
            return api;
        }

        public async Task SeedAsync(ManagementApi api)
        {
            using var owner = api.As(User);
            Organization = await api.CreateOrganizationAsync("Seed " + Faker.Company.CompanyName(), owner);
            Project = await api.CreateProjectAsync(Organization, "Project " + Faker.Random.AlphaNumeric(8), owner);
            Source = await api.CreateDatabaseAsync(Organization, Project, "source", owner);
            Neighbor = await api.CreateDatabaseAsync(Organization, Project, "neighbor", owner);
            ApiKey = await api.Server.IssueAsync(Organization, "documents:read", "documents:write");
            using var data = api.DataClient(ApiKey);
            await WriteDocumentAsync(data, "source");
            await WriteDocumentAsync(data, "neighbor");
        }

        public async Task WriteDocumentAsync(HttpClient data, string database)
        {
            using var collection = await data.PostAsJsonAsync(Uri($"/v1/projects/{Project}/databases/{database}/collections"), new { name = "people" });
            await Assert.That(collection.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict).IsTrue();
            using var write = await data.PutAsJsonAsync(Document(database), new { name = Person });
            write.EnsureSuccessStatusCode();
        }

        public async Task AssertDocumentAsync(HttpClient data, string database)
        {
            using var response = await data.GetAsync(Document(database));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(Person);
        }

        public async Task AssertPreservedAsync(ManagementApi api, HttpClient owner)
        {
            foreach (var id in new[] { Source, Neighbor })
            {
                using var read = await owner.GetAsync(Uri(Databases + "/" + id));
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(File.Exists(Path(id))).IsTrue();
            }

            using var data = api.DataClient(ApiKey);
            await AssertDocumentAsync(data, "source");
            await AssertDocumentAsync(data, "neighbor");
            // The preserved source still accepts writes after recovery, rather than merely listing in the catalog.
            using var written = await data.PutAsJsonAsync(Document("source"), new { name = Person });
            written.EnsureSuccessStatusCode();
        }

        public async Task AssertUnregisteredAsync(ManagementApi api, HttpClient owner, string database, string? replacement = null)
        {
            using var missing = await owner.GetAsync(Uri(Databases + "/" + database));
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await CatalogAsync(api, $"projects/{Project}/databases", database)).IsNull();
            await Assert.That(await CatalogAsync(api, $"projects/{Project}/database_names", TargetName))
                .IsEqualTo(replacement is null ? null : JsonSerializer.Serialize(replacement));
            using var data = api.DataClient(ApiKey);
            using var gone = await data.GetAsync(Document(TargetName));
            await Assert.That(gone.StatusCode).IsEqualTo(replacement is null ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        }

        public async Task WriteArtifactsAsync(string database, bool sidecars)
        {
            var path = Path(database);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".{database}.{EntityIds.New()}.creating");
            foreach (var suffix in Sidecars.Prepend(string.Empty))
            {
                await File.WriteAllBytesAsync(temporary + suffix, Faker.Random.Bytes(31));
                if (sidecars && suffix.Length != 0 && suffix != ".key")
                {
                    await File.WriteAllBytesAsync(path + suffix, Faker.Random.Bytes(31));
                }
            }
        }

        public async Task AssertFilesGoneAsync(string database)
        {
            foreach (var suffix in Sidecars.Prepend(string.Empty))
            {
                await Assert.That(File.Exists(Path(database) + suffix)).IsFalse();
            }

            var directory = System.IO.Path.GetDirectoryName(Path(database))!;
            await Assert.That(System.IO.Directory.Exists(directory)
                ? System.IO.Directory.GetFiles(directory, $".{database}.*.creating*") : []).IsEmpty();
        }

        public Task CleanupCompletion => _cleanup.Value;

        public ValueTask DisposeAsync() => new(TerraBaseTestServer.WaitForCleanupAsync(
            CleanupCompletion, TerraBaseTestServer.ReportCleanupFailure, _cancellationToken));

        private async Task CleanupAsync()
        {
            try
            {
                // A canceled disposal or startup wait does not end the host's ownership of this directory.
                await Task.WhenAll(_started.Select(api => api.CleanupCompletion).Concat(_failedStarts));
            }
            finally
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private Uri Document(string database) => Uri($"/v1/projects/{Project}/databases/{database}/collections/people/documents/by-key?id={DocumentId}");
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
