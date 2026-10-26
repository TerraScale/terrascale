using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CatalogAnchorMetricsTests
{
    [Test]
    public async Task A_node_without_an_anchor_bucket_exports_zero_timeouts()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        await CountAsync(server, 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Own_deadlines_count_once_including_waiting_for_the_anchor_writer(bool queued)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var host = await BackupShippingHost.StartAsync();
        var gate = host.Api.Server.Services.GetRequiredService<ICatalogGate>();
        await CountAsync(host.Api.Server, 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.S3.BeforePut = async (key, cancellation) =>
        {
            if (key.StartsWith("catalog-anchor/", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                // Hold the owner past the waiter's deadline to exercise semaphore ownership on cancellation.
                if (queued)
                {
                    await release.Task.WaitAsync(token);
                    cancellation.ThrowIfCancellationRequested();
                }
                else
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                }
            }
        };
        var owner = gate.TryAnchorAsync(token);
        try
        {
            await entered.Task.WaitAsync(token);
            var attempt = queued ? gate.TryAnchorAsync(token) : owner;
            host.Time.Advance(CatalogGate.AnchorTimeout);
            await Assert.That(await attempt.WaitAsync(token)).IsFalse();
            await CountAsync(host.Api.Server, 1);
            if (queued)
            {
                await Assert.That(owner.IsCompleted).IsFalse();
                release.TrySetResult();
                await Assert.That(await owner.WaitAsync(token)).IsFalse();
            }

            host.S3.BeforePut = null;
            await Assert.That(await gate.TryAnchorAsync(token)).IsTrue();
            await CountAsync(host.Api.Server, queued ? 2 : 1);
        }
        finally
        {
            release.TrySetResult();
            await owner.WaitAsync(token);
        }
    }

    [Test]
    [Arguments("caller")]
    [Arguments("io")]
    [Arguments("unrelated-cancellation")]
    public async Task Caller_cancellation_and_unrelated_failures_do_not_count(string failure)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var host = await BackupShippingHost.StartAsync();
        var gate = host.Api.Server.Services.GetRequiredService<ICatalogGate>();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.S3.BeforePut = async (key, cancellation) =>
        {
            if (key.StartsWith("catalog-anchor/", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                if (failure == "io")
                {
                    throw new IOException("test anchor storage failure");
                }

                if (failure == "unrelated-cancellation")
                {
                    throw new OperationCanceledException("test unrelated cancellation");
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
        };
        try
        {
            var attempt = gate.TryAnchorAsync(caller.Token);
            await entered.Task.WaitAsync(token);
            if (failure != "caller")
            {
                await Assert.That(await attempt.WaitAsync(token)).IsFalse();
            }
            else
            {
                await caller.CancelAsync();
                await Assert.That(async () => await attempt.WaitAsync(token)).Throws<OperationCanceledException>();
            }
        }
        finally
        {
            host.S3.BeforePut = null;
            await caller.CancelAsync();
        }

        host.Time.Advance(CatalogGate.AnchorTimeout);
        await CountAsync(host.Api.Server, 0);
    }

    [Test]
    public async Task Shutdown_cancellation_of_a_periodic_anchor_does_not_count()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var host = await BackupShippingHost.StartAsync();
        var gate = (CatalogGate)host.Api.Server.Services.GetRequiredService<ICatalogGate>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.S3.BeforePut = async (key, cancellation) =>
        {
            if (key.StartsWith("catalog-anchor/", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
        };
        var lifetime = host.Api.Server.Services.GetRequiredService<IHostApplicationLifetime>();
        var meter = host.Api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long stoppedCount = -1;
        var observations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "terrabase.catalog.anchor.timeouts" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            Interlocked.Exchange(ref stoppedCount, value);
            Interlocked.Increment(ref observations);
        });
        listener.Start();
        // Listeners close during host shutdown. Observe this host's counter after its services stop,
        // before the meter is disposed, and retain the result outside the host.
        using var stopped = lifetime.ApplicationStopped.Register(listener.RecordObservableInstruments);
        try
        {
            await CountAsync(host.Api.Server, 0);
            await host.Api.CreateOrganizationAsync("periodic anchor change");
            host.Time.Advance(TimeSpan.FromSeconds(15));
            await entered.Task.WaitAsync(token);
            // The in-flight request retains its hook; let the final shutdown anchor proceed normally.
            host.S3.BeforePut = null;
            await host.DisposeAsync();
            await Assert.That(lifetime.ApplicationStopping.IsCancellationRequested).IsTrue();
            await Assert.That(gate.ExecuteTask!.IsCanceled).IsTrue();
            await Assert.That(Volatile.Read(ref observations)).IsEqualTo(1);
            await Assert.That(Interlocked.Read(ref stoppedCount)).IsEqualTo(0);
        }
        finally
        {
            host.S3.BeforePut = null;
        }
    }

    private static async Task CountAsync(TerraBaseTestServer server, int expected)
    {
        using var client = new HttpClient { BaseAddress = server.MetricsAddress };
        var metrics = await client.GetStringAsync(new Uri("/metrics", UriKind.Relative), TestContext.Current!.Execution.CancellationToken);
        await Assert.That(metrics).Contains("# TYPE terrabase_catalog_anchor_timeouts_total counter");
        var sample = metrics.Split('\n').Single(line => line.StartsWith("terrabase_catalog_anchor_timeouts_total{", StringComparison.Ordinal));
        await Assert.That(sample).IsEqualTo(FormattableString.Invariant($"terrabase_catalog_anchor_timeouts_total{{otel_scope_name=\"TerraScale.TerraBase\"}} {expected}"));
    }
}
