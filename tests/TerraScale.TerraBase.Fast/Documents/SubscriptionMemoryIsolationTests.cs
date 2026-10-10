using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Fast.Management;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class SubscriptionMemoryIsolationTests
{
    private const long Budget = 64 * 1024 * 1024;
    private const long HobbyBytes = Budget / 16;

    [Test]
    public async Task Nonempty_events_return_organization_credit_after_flush_before_disconnect()
    {
        using var deadline = Deadline();
        await using var api = await StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        // The authority verifier reserves 4 MiB; bootstrap the binding before testing Hobby's 4 MiB allowance.
        await api.SetPlanAsync(api.User, database.Organization, "enterprise", deadline.Token);
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        await api.SetPlanAsync(api.User, database.Organization, "hobby", deadline.Token);
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var padding = new Faker().Random.String2(192 * 1024, "abcdef");
        await WriteAsync(data, database, padding, 0, deadline.Token);
        using var subscriber = Subscriber(api, database);
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        var tenant = Context(database);
        const int Changes = 8;
        var waits = Enumerable.Range(0, Changes + 1).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitCount = 0;
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && instrument.Name is "terrabase.shape.live.waits" or "terrabase.subscriptions.active")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "terrabase.shape.live.waits" && value == 1)
            {
                var index = Interlocked.Increment(ref waitCount) - 1;
                if (index < waits.Length)
                {
                    waits[index].TrySetResult();
                }
            }
            else if (instrument.Name == "terrabase.subscriptions.active" && value == -1)
            {
                closed.TrySetResult();
            }
        });
        listener.Start();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var response = await subscriber.GetAsync(Route(database), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream);
        var (snapshotKind, snapshotBody) = await EventAsync(reader, deadline.Token);
        await ShapeEventAsync(snapshotKind, snapshotBody, "initial snapshot");
        await Assert.That(snapshotBody.GetProperty("changes").GetArrayLength()).IsEqualTo(1);
        while (!snapshotBody.GetProperty("up_to_date").GetBoolean())
        {
            (snapshotKind, snapshotBody) = await EventAsync(reader, deadline.Token);
            await ShapeEventAsync(snapshotKind, snapshotBody, "snapshot continuation");
        }
        for (var i = 0; i <= Changes; i++)
        {
            await waits[i].Task.WaitAsync(deadline.Token);
            // The feed is still open: only its fixed request scratch remains charged to Hobby.
            await CreditAsync(budget, tenant, HobbyBytes - 128 * 1024, deadline.Token);
            if (i < Changes)
            {
                var offset = await WriteAsync(data, database, padding, i + 1, deadline.Token);
                var (changeKind, changeBody) = await EventAsync(reader, deadline.Token);
                await ShapeEventAsync(changeKind, changeBody, $"change iteration {i + 1} of {Changes}");
                await Assert.That(changeBody.GetProperty("next_offset").GetString()).IsEqualTo(offset);
                await Assert.That(changeBody.GetProperty("changes")[0].GetProperty("ops")[0]
                    .GetProperty("document").GetProperty("iteration").GetInt32()).IsEqualTo(i + 1);
            }
        }

        await cancel.CancelAsync();
        response.Dispose();
        await closed.Task.WaitAsync(deadline.Token);
        await CreditAsync(budget, tenant, HobbyBytes, deadline.Token);
        using var processCredit = budget.TryReserve(MemoryLane.Foreground, 40 * 1024 * 1024, tenant: null);
        await Assert.That(processCredit).IsNotNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Process_or_tenant_refusal_before_headers_recovers_after_release(bool tenantPressure)
    {
        using var deadline = Deadline();
        await using var api = await StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        // The authority verifier reserves 4 MiB; bootstrap the binding before testing Hobby's 4 MiB allowance.
        await api.SetPlanAsync(api.User, database.Organization, "enterprise", deadline.Token);
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        await api.SetPlanAsync(api.User, database.Organization, "hobby", deadline.Token);
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        await WriteAsync(data, database, new Faker().Random.String2(128 * 1024, "abcdef"), 0, deadline.Token);
        using var subscriber = Subscriber(api, database);
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        using (var held = budget.TryReserve(MemoryLane.Foreground,
            tenantPressure ? HobbyBytes - 128 * 1024 : 40 * 1024 * 1024, tenantPressure ? Context(database) : null))
        {
            await Assert.That(held).IsNotNull();
            using var refused = await subscriber.GetAsync(Route(database), deadline.Token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            var error = await refused.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            await Assert.That(error.GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo(tenantPressure ? "tenant_memory" : "subscription_busy");
            if (tenantPressure)
            {
                await Assert.That(refused.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            }
        }

        using var admitted = await subscriber.GetAsync(Route(database), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        admitted.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await admitted.Content.ReadAsStreamAsync(deadline.Token));
        var (admittedKind, admittedBody) = await EventAsync(reader, deadline.Token);
        await ShapeEventAsync(admittedKind, admittedBody, $"admitted after {(tenantPressure ? "tenant" : "process")} pressure");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Tenant_result_refusal_after_headers_sends_a_terminal_error_and_a_retry_recovers(bool deletions)
    {
        using var deadline = Deadline();
        ReadGate? gate = null;
        await using var api = await ManagementApi.StartAsync(settings => settings["memory-budget"] = "67108864", services =>
        {
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
            services.AddSingleton<IShapeService>(provider => gate = new ReadGate(ActivatorUtilities.CreateInstance<ShapeService>(provider)));
        }, managementCredential: true);
        var database = await api.CreateManagedDatabaseAsync();
        // The authority verifier reserves 4 MiB; bootstrap the binding before testing Hobby's 4 MiB allowance.
        await api.SetPlanAsync(api.User, database.Organization, "enterprise", deadline.Token);
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        await api.SetPlanAsync(api.User, database.Organization, "hobby", deadline.Token);
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var padding = new Faker().Random.String2(128 * 1024, "abcdef");
        const int Tombstones = 32;
        if (deletions)
        {
            for (var i = 0; i < Tombstones; i++)
            {
                using var written = await data.PutAsJsonAsync(
                    ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", $"deleted{i}")), new { iteration = i }, deadline.Token);
                written.EnsureSuccessStatusCode();
            }
        }
        else
        {
            await WriteAsync(data, database, padding, 0, deadline.Token);
        }

        using var subscriber = Subscriber(api, database);
        using var response = await subscriber.GetAsync(Route(database), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var (initialKind, initialBody) = await EventAsync(reader, deadline.Token);
        await ShapeEventAsync(initialKind, initialBody, $"initial snapshot, deletions={deletions}");
        while (!initialBody.GetProperty("up_to_date").GetBoolean())
        {
            (initialKind, initialBody) = await EventAsync(reader, deadline.Token);
            await ShapeEventAsync(initialKind, initialBody, $"snapshot continuation, deletions={deletions}");
        }
        await gate!.Entered.Task.WaitAsync(deadline.Token);
        try
        {
            if (deletions)
            {
                // All writes precede result pressure; replay contains only bodyless deletion rows.
                for (var i = 0; i < Tombstones; i++)
                {
                    using var deleted = await data.DeleteAsync(
                        ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", $"deleted{i}")), deadline.Token);
                    deleted.EnsureSuccessStatusCode();
                }
            }
            else
            {
                await WriteAsync(data, database, padding, 1, deadline.Token);
            }

            var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
            using (var held = budget.TryReserve(MemoryLane.Foreground, HobbyBytes - 128 * 1024, Context(database)))
            {
                await Assert.That(held).IsNotNull();
                // Request scratch leaves under 64 KiB: enough to send an error, not to admit 32 tombstones at 2 KiB each.
                gate.Continue.TrySetResult();
                var (terminalKind, terminalBody) = await EventAsync(reader, deadline.Token);
                await Assert.That(terminalKind).IsEqualTo("error");
                await Assert.That(terminalBody.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("tenant_memory");
                await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsNull();
            }

            await CreditAsync(budget, Context(database), HobbyBytes, deadline.Token);
            using (var processCredit = budget.TryReserve(MemoryLane.Foreground, 40 * 1024 * 1024, tenant: null))
            {
                await Assert.That(processCredit).IsNotNull();
            }

            using var retry = await subscriber.GetAsync(Route(database), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            retry.EnsureSuccessStatusCode();
            using var retryReader = new StreamReader(await retry.Content.ReadAsStreamAsync(deadline.Token));
            var (retriedKind, retriedBody) = await EventAsync(retryReader, deadline.Token);
            await ShapeEventAsync(retriedKind, retriedBody, $"retry after result pressure, deletions={deletions}");
            if (deletions)
            {
                await Assert.That(retriedBody.GetProperty("changes")[0].GetProperty("ops").GetArrayLength()).IsEqualTo(0);
            }
        }
        finally
        {
            gate.Continue.TrySetResult();
        }
    }

    [Test]
    public async Task A_query_deadline_after_headers_sends_a_terminal_error_and_returns_credit()
    {
        using var deadline = Deadline();
        var clock = new SteppingClock();
        await using var api = await StartAsync(clock);
        var database = await api.CreateManagedDatabaseAsync();
        // The authority verifier reserves 4 MiB; bootstrap the binding before testing Hobby's 4 MiB allowance.
        await api.SetPlanAsync(api.User, database.Organization, "enterprise", deadline.Token);
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        await api.SetPlanAsync(api.User, database.Organization, "hobby", deadline.Token);
        using var data = await api.CreateClientAsync(database.Organization, "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var padding = new Faker().Random.String2(128 * 1024, "abcdef");
        await WriteAsync(data, database, padding, 0, deadline.Token);
        using var subscriber = Subscriber(api, database);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && instrument.Name is "terrabase.shape.live.waits" or "terrabase.subscriptions.active")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "terrabase.shape.live.waits")
            {
                if (value == 1)
                {
                    waiting.TrySetResult();
                }
                else if (value == -1)
                {
                    // Arm as the live wait ends, after the wake-up write commits.
                    clock.Armed = true;
                }
            }
            else if (instrument.Name == "terrabase.subscriptions.active" && value == -1)
            {
                closed.TrySetResult();
            }
        });
        listener.Start();
        using var response = await subscriber.GetAsync(Route(database), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var (snapshotKind, snapshotBody) = await EventAsync(reader, deadline.Token);
        await ShapeEventAsync(snapshotKind, snapshotBody, "deadline snapshot");
        await Assert.That(snapshotBody.GetProperty("changes").GetArrayLength()).IsEqualTo(1);
        while (!snapshotBody.GetProperty("up_to_date").GetBoolean())
        {
            (snapshotKind, snapshotBody) = await EventAsync(reader, deadline.Token);
            await ShapeEventAsync(snapshotKind, snapshotBody, "deadline snapshot continuation");
        }

        await waiting.Task.WaitAsync(deadline.Token);
        await WriteAsync(data, database, padding, 1, deadline.Token);
        var (terminalKind, terminalBody) = await EventAsync(reader, deadline.Token);
        await Assert.That(terminalKind).IsEqualTo("error");
        await Assert.That(terminalBody.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_query");
        await Assert.That(terminalBody.GetProperty("error").GetProperty("message").GetString()).IsEqualTo("query too expensive");
        await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsNull();
        await closed.Task.WaitAsync(deadline.Token);
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        await CreditAsync(budget, Context(database), HobbyBytes, deadline.Token);
        using var processCredit = budget.TryReserve(MemoryLane.Foreground, 40 * 1024 * 1024, tenant: null);
        await Assert.That(processCredit).IsNotNull();
    }

    private static Task<ManagementApi> StartAsync(TimeProvider? queryClock = null) =>
        ManagementApi.StartAsync(settings => settings["memory-budget"] = "67108864", services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, queryClock ?? new FakeTimeProvider()), managementCredential: true);

    private static TenantContext Context(ManagedDatabaseRef database) => new(database.Organization, PlanTier.Hobby, database.Project, database.Id);

    private static Uri Route(ManagedDatabaseRef database) =>
        new($"/v1/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe", UriKind.Relative);

    private static HttpClient Subscriber(ManagementApi api, ManagedDatabaseRef database) => api.WithToken(api.Issuer.Mint(api.User, token =>
    {
        token.Claims["project_id"] = database.Project;
        token.Claims["scope"] = "shapes:read";
    }));

    private static CancellationTokenSource Deadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return deadline;
    }

    private static async Task<string> WriteAsync(HttpClient data, ManagedDatabaseRef database, string padding, int iteration, CancellationToken cancellationToken)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "hot")),
            new { padding, iteration }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("offset").GetString()!;
    }

    private static async Task CreditAsync(IMemoryBudget budget, TenantContext tenant, long bytes, CancellationToken cancellationToken)
    {
        // Worker completion and the listener's final frame can race the caller; await actual admission, without sleeps.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var credit = budget.TryReserve(MemoryLane.Foreground, bytes, tenant);
            if (credit is not null)
            {
                return;
            }

            await Task.Yield();
        }
    }

    private static async Task<(string Kind, JsonElement Body)> EventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var kind = "";
        string? data = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 && data is not null)
            {
                using var json = JsonDocument.Parse(data);
                return (kind, json.RootElement.Clone());
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                kind = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
        }

        throw new InvalidOperationException("the subscription closed before its next event");
    }

    private static async Task ShapeEventAsync(string kind, JsonElement body, string context)
    {
        var diagnostic = $"{context}: event {kind}";
        if (kind != "shape")
        {
            var text = body.GetRawText();
            diagnostic += $": {text[..Math.Min(text.Length, 4096)]}";
        }

        await Assert.That(kind).IsEqualTo("shape").Because(diagnostic);
    }

    private sealed class ReadGate(IShapeService inner) : IShapeService
    {
        private int _block = 1;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken, bool drainOnCancellation = false)
        {
            if (request.Live && Interlocked.Exchange(ref _block, 0) == 1)
            {
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }

            return await inner.ReadAsync(request, cancellationToken, drainOnCancellation);
        }
    }
}
