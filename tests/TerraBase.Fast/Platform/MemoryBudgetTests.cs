using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Fast.Storage;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
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

                    var replacement = copy
                        ? setting switch
                        {
                            "storage-threads" => options with { StorageThreads = checked((int)value) },
                            "storage-queue" => options with { StorageQueue = checked((int)value) },
                            _ => options with { MemoryBudget = value },
                        }
                        : new TerraBaseOptions
                        {
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

        using var huge = budget.TryReserve(MemoryLane.Foreground, long.MaxValue);
        using var rest = budget.TryReserve(MemoryLane.Bulk, Budget - 1);
        using var over = budget.TryReserve(MemoryLane.Bulk, 1);

        await Assert.That(first).IsNotNull();
        await Assert.That(huge).IsNull();
        await Assert.That(rest).IsNotNull();
        await Assert.That(over).IsNull();
        first!.Dispose();
        rest!.Dispose();

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

        var workers = Enumerable.Range(0, Workers).Select(_ => Task.Factory.StartNew(() =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var oversized = budget.TryReserve(MemoryLane.Bulk, Budget + 1);
                using var reservation = budget.TryReserve(MemoryLane.Foreground, Size);

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

        using var all = budget.TryReserve(MemoryLane.Foreground, Budget);
        await Assert.That(all).IsNotNull();
        using var excess = budget.TryReserve(MemoryLane.Foreground, 1);
        await Assert.That(excess).IsNull();
    }

    [Test]
    public async Task Invalidated_and_cached_entries_return_their_reservations()
    {

        const int Padding = 768 * 1024;
        var management = await ManagementApi.StartAsync(values => values["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        var server = management.Server;
        IMemoryBudget budget;
        try
        {
            budget = server.Services.GetRequiredService<IMemoryBudget>();
            using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
            using (var owner = management.Service(tenant.User))
            using (var changed = await owner.PutAsJsonAsync(
                new Uri($"/v1/management/organizations/{tenant.OrganizationId}/plan", UriKind.Relative), new { plan = "enterprise" }))
            {
                changed.EnsureSuccessStatusCode();
            }

            var client = tenant.Data;
            using (var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/shop", UriKind.Relative), null))
            {
                created.EnsureSuccessStatusCode();
            }

            await StorageFixture.CreateCollectionAsync(tenant, "shop", "items");
            var uri = new Uri($"/v1/projects/{tenant.ProjectId}/databases/shop/collections/items/documents/by-key?id={StorageFixture.Id("big")}", UriKind.Relative);
            using (var written = await client.PutAsJsonAsync(uri, new { padding = new string('x', Padding) }))
            {
                written.EnsureSuccessStatusCode();
            }

            using (var read = await client.GetAsync(uri))
            {
                read.EnsureSuccessStatusCode();
            }

            await Assert.That(IsHeld(budget, Budget - Padding)).IsTrue();

            await StorageFixture.ExecuteAsync(await tenant.FileAsync(server, "shop"), """UPDATE "items" SET doc = json_set(doc, '$.disk', 1);""");
            await Assert.That(await client.GetStringAsync(uri)).DoesNotContain("disk");

            await StorageFixture.ExecuteAsync(await tenant.FileAsync(server, "shop"), """UPDATE "items" SET doc = json_remove(doc, '$.disk');""");

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

    private static bool IsHeld(IMemoryBudget budget, long bytes)
    {
        using var reservation = budget.TryReserve(MemoryLane.Foreground, bytes);
        return reservation is null;
    }

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
