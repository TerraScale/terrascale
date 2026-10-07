using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Fast.Storage;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class MemoryBudgetTests
{
    private const long Budget = 4 * 1024 * 1024;

    [Test]
    [Arguments("storage-threads", -1L)]
    [Arguments("storage-threads", 0L)]
    [Arguments("storage-threads", 1025L)]
    [Arguments("storage-queue", -1L)]
    [Arguments("storage-queue", 0L)]
    [Arguments("storage-queue", 1_000_001L)]
    [Arguments("memory-budget", -1L)]
    [Arguments("memory-budget", 0L)]
    [Arguments("memory-budget", 1024L * 1024 - 1)]
    public async Task Programmatic_options_reject_invalid_limits_without_registration_validation(string setting, long value)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        foreach (var copy in new[] { false, true })
        {
            var error = await Assert.ThrowsAsync<TerraBaseConfigurationException>(async () =>
            {
                await using var server = await TerraBaseTestServer.StartAsync(services: services =>
                {
                    var options = (TerraBaseOptions)services.Single(service => service.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
                    // Replace the singleton directly: no feature registration may provide the validation.
                    var replacement = copy
                        ? setting switch
                        {
                            "storage-threads" => options with { StorageThreads = checked((int)value) },
                            "storage-queue" => options with { StorageQueue = checked((int)value) },
                            _ => options with { MemoryBudget = value },
                        }
                        : new TerraBaseOptions
                        {
                            AdminPort = options.AdminPort,
                            DataDirectory = options.DataDirectory,
                            StorageThreads = setting == "storage-threads" ? checked((int)value) : options.StorageThreads,
                            StorageQueue = setting == "storage-queue" ? checked((int)value) : options.StorageQueue,
                            MemoryBudget = setting == "memory-budget" ? value : options.MemoryBudget,
                        };
                    services.Replace(ServiceDescriptor.Singleton(replacement));
                });
            });
            await Assert.That(error!.Message).Contains(setting);
        }
    }

    [Test]
    [Arguments(-1L)]
    [Arguments(0L)]
    [Arguments(1L)]
    [Arguments(1024L * 1024 - 1)]
    public async Task Programmatic_memory_registration_rejects_invalid_budgets_before_startup(long bytes)
    {
        var error = await Assert.ThrowsAsync<TerraBaseConfigurationException>(async () =>
        {
            await using var server = await TerraBaseTestServer.StartAsync(services: services =>
            {
                var options = (TerraBaseOptions)services.Single(service => service.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
                services.AddTerraBaseMemory(options with { MemoryBudget = bytes });
            });
        });
        await Assert.That(error!.Message).Contains("memory-budget");
    }

    [Test]
    [Arguments(TerraBaseOptions.MinMemoryBudget)]
    [Arguments(8L * 1024 * 1024)]
    public async Task Replaced_options_control_memory_budget_and_storage_admission(long bytes)
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            var options = (TerraBaseOptions)services.Single(service => service.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(options with { MemoryBudget = bytes, StorageQueue = 2 }));
        });
        using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        var budget = server.Services.GetRequiredService<IMemoryBudget>();
        using var all = budget.TryReserve(MemoryLane.Foreground, bytes);
        using var excess = budget.TryReserve(MemoryLane.Foreground, 1);
        await Assert.That(all).IsNotNull();
        await Assert.That(excess).IsNull();
        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var first = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await using var second = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.ThrowsAsync<StorageBusyException>(async () =>
        {
            await using var refused = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        });
    }

    [Test]
    public async Task Reservations_never_exceed_the_budget_and_refusals_leave_it_untouched()
    {
        await using var server = await StartAsync();
        var budget = server.Services.GetRequiredService<IMemoryBudget>();
        var meter = server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "terrabase.memory.reserved" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, reserved, _, _) => total = reserved);
        listener.Start();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        {
            using var negative = budget.TryReserve(MemoryLane.Foreground, -1);
            return Task.CompletedTask;
        });

        using var first = budget.TryReserve(MemoryLane.Foreground, 1);
        // Would overflow the counter if it were added before the check.
        using var huge = budget.TryReserve(MemoryLane.Foreground, long.MaxValue);
        using var rest = budget.TryReserve(MemoryLane.Bulk, Budget - 1);
        using var over = budget.TryReserve(MemoryLane.Replication, 1);

        await Assert.That(first).IsNotNull();
        await Assert.That(huge).IsNull();
        await Assert.That(rest).IsNotNull();
        await Assert.That(over).IsNull();
        first!.Dispose();
        rest!.Dispose();
        // Disposing twice returns the bytes once.
        rest.Dispose();
        using var tooLarge = budget.TryReserve(MemoryLane.Foreground, Budget + 1);
        await Assert.That(tooLarge).IsNull();
        listener.RecordObservableInstruments();
        await Assert.That(total).IsEqualTo(0);
        using var all = budget.TryReserve(MemoryLane.Foreground, Budget);
        await Assert.That(all).IsNotNull();
        await Assert.That(all!.Bytes).IsEqualTo(Budget);
        listener.RecordObservableInstruments();
        await Assert.That(total).IsEqualTo(Budget);
        using var excess = budget.TryReserve(MemoryLane.Foreground, 1);
        await Assert.That(excess).IsNull();
        listener.RecordObservableInstruments();
        await Assert.That(total).IsEqualTo(Budget);
        // A zero-byte reservation remains valid when capacity is exhausted and changes no accounting.
        using var zero = budget.TryReserve(MemoryLane.Foreground, 0);
        await Assert.That(zero).IsNotNull();
        await Assert.That(zero!.Bytes).IsEqualTo(0);
        zero.Dispose();
        using var stillExhausted = budget.TryReserve(MemoryLane.Foreground, 1);
        await Assert.That(stillExhausted).IsNull();
        all!.Dispose();
        using var recovered = budget.TryReserve(MemoryLane.Foreground, Budget);
        await Assert.That(recovered).IsNotNull();
    }

    [Test]
    public async Task Concurrent_reservations_never_exceed_the_budget()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const int Workers = 8;
        const int Rounds = 500;
        const long Size = Budget / 4;
        await using var server = await StartAsync(cancellationToken: deadline.Token);
        var budget = server.Services.GetRequiredService<IMemoryBudget>();
        var meter = server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var measurements = new Lock();
        long peak = 0;
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            // Every parallel test owns a meter factory; sample only this server's budget.
            if (instrument.Name == "terrabase.memory.reserved" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, reserved, _, _) =>
        {
            lock (measurements)
            {
                peak = Math.Max(peak, reserved);
                total = reserved;
            }
        });
        listener.Start();
        var cancellationToken = deadline.Token;
        // Independent dedicated workers: no rendezvous can strand another worker after a failure.
        var workers = Enumerable.Range(0, Workers).Select(_ => Task.Factory.StartNew(() =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var oversized = budget.TryReserve(MemoryLane.Bulk, Budget + 1);
                using var reservation = budget.TryReserve(MemoryLane.Foreground, Size);
                // Sample the budget's actual counter while this reservation is still held.
                listener.RecordObservableInstruments();
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var completed = Task.WhenAll(workers);
        Exception? primaryFailure = null;
        try
        {
            await completed.WaitAsync(cancellationToken);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            deadline.Cancel();
            try
            {
                await completed;
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                if (!ReferenceEquals(primaryFailure, cleanupFailure))
                {
                    primaryFailure.Data["CleanupFailure"] = cleanupFailure;
                }
            }
        }

        listener.RecordObservableInstruments();

        await Assert.That(peak).IsGreaterThanOrEqualTo(Size);
        await Assert.That(peak).IsLessThanOrEqualTo(Budget);
        await Assert.That(total).IsEqualTo(0);
        using var all = budget.TryReserve(MemoryLane.Foreground, Budget);
        await Assert.That(all).IsNotNull();
    }

    [Test]
    public async Task Disposing_a_reservation_concurrently_returns_it_once()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var server = await StartAsync(cancellationToken: deadline.Token);
        var budget = server.Services.GetRequiredService<IMemoryBudget>();
        var cancellationToken = deadline.Token;
        Task workers = Task.CompletedTask;
        try
        {
            for (var round = 0; round < 1_000; round++)
            {
                using var reservation = budget.TryReserve(MemoryLane.Foreground, Budget / 2)!;
                workers = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
                {
                    reservation.Dispose();
                }, cancellationToken)));
                await workers.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            deadline.Cancel();
        }

        // Returned exactly once each: the whole budget is free, and no more than the whole budget.
        using var all = budget.TryReserve(MemoryLane.Foreground, Budget);
        await Assert.That(all).IsNotNull();
        using var excess = budget.TryReserve(MemoryLane.Foreground, 1);
        await Assert.That(excess).IsNull();
    }

    [Test]
    public async Task Engines_starting_together_never_lower_the_process_heap_limit()
    {
        const string ChildFlag = "TERRABASE_TEST_ENGINES_HEAP_LIMIT_CHILD";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            // SQLite's limit cannot be restored safely while other tests are using it.
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test executable path is unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // dotnet <suite.dll> and the suite's apphost both run without rebuilding.
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            }

            start.ArgumentList.Add("--treenode-filter");
            start.ArgumentList.Add("/TerraBase.Fast/TerraBase.Fast.Platform/MemoryBudgetTests/" + nameof(Engines_starting_together_never_lower_the_process_heap_limit));
            start.ArgumentList.Add("--minimum-expected-tests");
            start.ArgumentList.Add("1");
            start.ArgumentList.Add("--no-progress");
            start.ArgumentList.Add("--disable-logo");
            start.ArgumentList.Add("--exit-on-process-exit");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.Environment[ChildFlag] = "1";

            using var child = Process.Start(start) ?? throw new InvalidOperationException("The isolated heap-limit test could not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var stdout = ReadChildOutputAsync(child.StandardOutput, deadline.Token);
            var stderr = ReadChildOutputAsync(child.StandardError, deadline.Token);
            // Retain actual exit even when the test's wait or its output readers are cancelled.
            var exited = child.WaitForExitAsync(CancellationToken.None);
            var completed = Task.WhenAll(exited, stdout, stderr);
            Exception? primaryFailure = null;
            try
            {
                await completed.WaitAsync(deadline.Token);
                if (child.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Isolated heap-limit test exited with code {child.ExitCode}.\n{await stdout}\n{await stderr}");
                }
            }
            catch (OperationCanceledException error)
            {
                var failure = new TimeoutException("The isolated heap-limit test exceeded two minutes.", error);
                primaryFailure = failure;
                throw failure;
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                deadline.Cancel();
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException) when (child.HasExited)
                {
                }
                finally
                {
                    // The bounded exit wait does not abandon completed's actual exit/readers join.
                    await StoragePoolTests.JoinCleanupAsync(
                        [exited.WaitAsync(TimeSpan.FromSeconds(30)), completed], primaryFailure);
                }
            }

            return;
        }

        // This process runs only this test, so ordinary 8-20 MiB budgets are enough.
        var budgets = Enumerable.Range(1, 4).Select(i => Budget * (i + 1)).ToArray();
        // Load the native library before reading its global state, without opening a database.
        using (new Microsoft.Data.Sqlite.SqliteConnection())
        {
        }

        // This isolated process owns its heap limit; process exit restores isolation without racing background work.
        await InitializeTogetherAsync(initialLimit: 0, expectedLimit: budgets.Max() / 4);
        var earlierLimit = budgets.Max() / 2;
        await InitializeTogetherAsync(initialLimit: earlierLimit, expectedLimit: earlierLimit);

        async Task InitializeTogetherAsync(long initialLimit, long expectedLimit)
        {
            SQLitePCL.raw.sqlite3_soft_heap_limit64(initialLimit);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var database = new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            var ready = 0;
            var initializations = Task.WhenAll(budgets.Select(InitializeAsync));
            Exception? primaryFailure = null;
            try
            {
                await initializations.WaitAsync(deadline.Token);
                await Assert.That(SQLitePCL.raw.sqlite3_soft_heap_limit64(-1)).IsEqualTo(expectedLimit);

                // A later smaller engine must preserve the largest earlier limit too.
                await using var smaller = await TerraBaseTestServer.StartAsync(settings =>
                    settings["memory-budget"] = budgets.Min().ToString(CultureInfo.InvariantCulture));
                using var response = await smaller.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
                response.EnsureSuccessStatusCode();
                await smaller.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(database, deadline.Token);
                await Assert.That(SQLitePCL.raw.sqlite3_soft_heap_limit64(-1)).IsEqualTo(expectedLimit);
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                deadline.Cancel();
                release.TrySetCanceled(deadline.Token);
                await StoragePoolTests.JoinCleanupAsync([initializations], primaryFailure);
            }

            async Task InitializeAsync(long bytes)
            {
                try
                {
                    // Every start owns its returned server, including a completion after caller cancellation.
                    await using var server = await TerraBaseTestServer.StartAsync(settings =>
                        settings["memory-budget"] = bytes.ToString(CultureInfo.InvariantCulture));
                    var engine = server.Services.GetRequiredService<IStorageEngine>();
                    if (Interlocked.Increment(ref ready) == budgets.Length)
                    {
                        // DI construction does not call SQLite; all hosts are started before the first database work.
                        await Assert.That(SQLitePCL.raw.sqlite3_soft_heap_limit64(-1)).IsEqualTo(initialLimit);
                        release.TrySetResult();
                    }
                    await release.Task.WaitAsync(deadline.Token);
                    await engine.CreateDatabaseAsync(database, deadline.Token);
                }
                catch (Exception error)
                {
                    release.TrySetException(error);
                    throw;
                }
            }
        }
    }

    private static async Task<string> ReadChildOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int MaxCharacters = 64 * 1024;
        var output = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            // Keep draining after the capture fills so verbose failures cannot block the child.
            output.Append(buffer, 0, Math.Min(read, MaxCharacters - output.Length));
        }

        return output.ToString();
    }

    [Test]
    public async Task Invalidated_and_cached_entries_return_their_reservations()
    {
        // About 1 MiB: most of the cache's share, so its reservation shows in the budget.
        const int Padding = 768 * 1024;
        var management = await ManagementApi.StartAsync(values => values["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        var server = management.Server;
        IMemoryBudget budget;
        try
        {
            budget = server.Services.GetRequiredService<IMemoryBudget>();
            using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
            var client = tenant.Data;
            using (var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/shop", UriKind.Relative), null))
            {
                created.EnsureSuccessStatusCode();
            }

            await StorageFixture.CreateCollectionAsync(tenant, "shop", "items");
            var uri = new Uri($"/v1/projects/{tenant.ProjectId}/databases/shop/collections/items/documents/{StorageFixture.Id("big")}", UriKind.Relative);
            using (var written = await client.PutAsJsonAsync(uri, new { padding = new string('x', Padding) }))
            {
                written.EnsureSuccessStatusCode();
            }

            using (var read = await client.GetAsync(uri))
            {
                read.EnsureSuccessStatusCode();
            }

            await Assert.That(IsHeld(budget, Budget - Padding)).IsTrue();
            // And the entry really is cached (not refused and about to be released): a change made behind the
            // server's back stays invisible.
            await StorageFixture.ExecuteAsync(await tenant.FileAsync(server, "shop"), """UPDATE documents SET body = json_set(body, '$.disk', 1);""");
            await Assert.That(await client.GetStringAsync(uri)).DoesNotContain("disk");

            // The read after a delete finds nothing to cache, so it must evict the invalidated entry itself.
            using (var deleted = await client.DeleteAsync(uri))
            {
                deleted.EnsureSuccessStatusCode();
            }

            using var gone = await client.GetAsync(uri);
            await Assert.That(gone.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NotFound);
            await Assert.That(await CanReserveAsync(budget, Budget - Padding)).IsTrue();

            using (var written = await client.PutAsJsonAsync(uri, new { padding = new string('y', Padding) }))
            {
                written.EnsureSuccessStatusCode();
            }

            using (var read = await client.GetAsync(uri))
            {
                read.EnsureSuccessStatusCode();
            }

            await Assert.That(IsHeld(budget, Budget - Padding)).IsTrue();
        }
        finally
        {
            await management.DisposeAsync();
        }

        // Stopping clears the cache, which returns every reservation.
        await Assert.That(await CanReserveAsync(budget, Budget)).IsTrue();
    }

    private static Task<TerraBaseTestServer> StartAsync(Action<Dictionary<string, string>>? settings = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TerraBaseTestServer.StartAsync(values =>
        {
            settings?.Invoke(values);
            values["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture);
        });
    }

    // A cache entry reserves its bytes before the response is sent, so this needs no waiting.
    private static bool IsHeld(IMemoryBudget budget, long bytes)
    {
        using var reservation = budget.TryReserve(MemoryLane.Foreground, bytes);
        return reservation is null;
    }

    // Eviction callbacks run on the thread pool shortly after an entry leaves the cache, so a release may
    // take a moment.
    private static async Task<bool> CanReserveAsync(IMemoryBudget budget, long bytes)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        while (true)
        {
            if (budget.TryReserve(MemoryLane.Foreground, bytes) is { } reservation)
            {
                reservation.Dispose();
                return true;
            }

            if (deadline.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(10, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        }
    }
}
