using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

public sealed class TursoSchedulingQosTests
{
    [Test]
    public async Task Hobby_saturation_preserves_a_worker_and_admission_for_paid_and_system_requests()
    {
        using var clock = new HeldHobbyClock();
        // Admission limits count every holder: a timer-driven system call admitted beside the held query would refuse the
        // second Hobby query, then satisfy the barrier below in its place. A clock that stands still keeps those timers quiet.
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "2";
            settings["storage-queue"] = "4";
        }, services => services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock), new FakeTimeProvider(DateTimeOffset.UtcNow));
        using var plans = api.Service(api.User);
        var hobby = await api.CreateManagedDatabaseAsync("hobby");
        var paid = await api.CreateManagedDatabaseAsync("paid");
        using var upgraded = await plans.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{paid.Organization}/plan"), new { plan = "pro" });
        upgraded.EnsureSuccessStatusCode();
        using var hobbyData = await api.CreateClientAsync(hobby.Organization, "documents:read", "documents:write");
        using var paidData = await api.CreateClientAsync(paid.Organization, "documents:read", "documents:write");
        foreach (var (data, database) in new[] { (hobbyData, hobby), (paidData, paid) })
        {
            await ManagementApi.CreateCollectionAsync(data, database, "items");
            using var warm = await data.GetAsync(ManagementApi.Uri($"{database.Data}/collections/items"));
            warm.EnsureSuccessStatusCode();
        }

        var io = api.Server.Services.GetRequiredService<TursoStorageEngine>().Io;
        using var cancelQueued = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var pending = new List<Task<HttpResponseMessage>>();
        var diagnostics = new TursoRunnerDiagnostics();
        var probeStarted = Stopwatch.GetTimestamp();
        Task<HttpResponseMessage>? running = null;
        Task<HttpResponseMessage>? queued = null;
        io.Diagnostics = diagnostics;
        Task ProbeAsync(string phase)
        {
            return ProbeRequestsAsync(phase, probeStarted, io, diagnostics, running, queued,
                TUnit.Core.TestContext.Current!.Execution.CancellationToken, cancelQueued.Token);
        }
        clock.Hold = true;
        try
        {
            running = QueryAsync(hobbyData, hobby, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            pending.Add(running);
            await clock.Entered.Task.WaitAsync(TUnit.Core.TestContext.Current.Execution.CancellationToken);
            await ProbeAsync("worker-held");
            queued = QueryAsync(hobbyData, hobby, cancelQueued.Token);
            pending.Add(queued);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(queued.IsCompleted || io.Admitted == 2 && io.Queued == 1));
            await ProbeAsync("global-barrier");
            await Assert.That(queued.IsCompleted).IsFalse();
            // The two admitted Hobby operations consume their lane; a third cannot take the paid slots.
            using var refused = await QueryAsync(hobbyData, hobby, TUnit.Core.TestContext.Current.Execution.CancellationToken);
            await ProbeAsync("third-response");
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            using var paidResponse = await QueryAsync(paidData, paid, TUnit.Core.TestContext.Current.Execution.CancellationToken).WaitAsync(TUnit.Core.TestContext.Current.Execution.CancellationToken);
            await ProbeAsync("paid-response");
            await Assert.That(paidResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var healthy = await api.Server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), TUnit.Core.TestContext.Current.Execution.CancellationToken);
            await ProbeAsync("ready-response");
            await Assert.That(healthy.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(running.IsCompleted).IsFalse();
            await Assert.That(queued.IsCompleted).IsFalse();

            cancelQueued.Cancel();
            try
            {
                using var unexpected = await queued;
                throw new InvalidOperationException("the queued request was not cancelled");
            }
            catch (OperationCanceledException)
            {
            }

            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(io.Queued == 0 && io.Admitted == 1));
            clock.Release();
            using var completed = await running;
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(io.Admitted == 0));
        }
        catch
        {
            await ProbeAsync("failure-before-cleanup");
            throw;
        }
        finally
        {
            io.Diagnostics = null;
            clock.Release();
            cancelQueued.Cancel();
            foreach (var request in pending)
            {
                await StoragePoolHelpers.DisposeResponseAsync(request);
            }
        }
    }

    [Test]
    public async Task A_cold_finite_plan_write_reuses_the_batch_admission_for_quota_accounting()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-queue"] = "1");
        var id = DocumentApi.NewId();
        var created = await api.PutAsync(id, new { n = 1 });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var read = await api.GetAsync(id);
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(api.Server.Services.GetRequiredService<TursoStorageEngine>().Io.Admitted == 0));
    }

    // Diagnostic capture only: keep the original request tasks, policies and assertions intact.
    private static async Task ProbeRequestsAsync(
        string phase, long started, TursoIoPool io, TursoRunnerDiagnostics diagnostics,
        Task<HttpResponseMessage>? running, Task<HttpResponseMessage>? queued, CancellationToken execution, CancellationToken queuedToken)
    {
        var output = TUnit.Core.TestContext.Current!.OutputWriter;
        var sample = diagnostics.Sample();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"QOS_PROBE phase={phase} elapsed_s={Stopwatch.GetElapsedTime(started).TotalSeconds:F6} execution_cancelled={execution.IsCancellationRequested} queued_cancelled={queuedToken.IsCancellationRequested} admitted={io.Admitted} queued={io.Queued} running={io.Running} submitted={sample.Submitted} begun={sample.Begun} completed={sample.Completed} resumed={sample.Resumed} cancelled={sample.Cancelled} cancelled_resumed={sample.CancelledResumed} aborted={sample.Aborted} lost={sample.Lost} oldest_queue_ticks={sample.OldestQueuedTicks} oldest_running_ticks={sample.OldestRunningTicks} oldest_resume_ticks={sample.OldestResumeTicks}"));
        foreach (var (name, request) in new[] { ("running", running), ("queued", queued) })
        {
            if (request is null)
            {
                continue;
            }

            var exception = request.Exception?.Flatten().InnerExceptions.FirstOrDefault()?.GetType().Name ?? "none";
            var status = 0;
            var code = "none";
            try
            {
                if (request.IsCompletedSuccessfully)
                {
                    var response = await request;
                    status = (int)response.StatusCode;
                    // PostAsJsonAsync has buffered the content. Never print bodies or exception messages.
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 and <= 64 } stable
                        && stable.All(character => char.IsAsciiLetterLower(character) || character == '_'))
                    {
                        code = stable;
                    }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A diagnostic read must not replace the original assertion or request outcome.
                code = "diagnostic_unavailable";
                exception = error.GetType().Name;
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"QOS_PROBE phase={phase} request={name} state={request.Status} faulted={request.IsFaulted} cancelled={request.IsCanceled} exception_type={exception} http_status={status} error_code={code}"));
        }
    }

    private static Task<HttpResponseMessage> QueryAsync(HttpClient data, ManagedDatabaseRef database, CancellationToken cancellationToken = default) =>
        data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/collections/items/query"), new { limit = 1 }, cancellationToken);

    // Hold actual query execution on its Turso worker, after authentication and plan resolution.
    // A synchronization barrier, rather than elapsed time, proves paid work finishes while Hobby is still executing.
    private sealed class HeldHobbyClock : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();

        public bool Hold { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long GetTimestamp()
        {
            if (Hold && QosContext.Current?.Plan == PlanTier.Hobby && Thread.CurrentThread.Name == "terrabase-turso")
            {
                Entered.TrySetResult();
                _release.Wait();
            }

            return TimeProvider.System.GetTimestamp();
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
