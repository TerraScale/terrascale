using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

// terrabase.database_create.stage_duration (docs/terrabase/database-create-stage-metrics.md): each stage of a create records
// once, with how it ended, and cleanup after a refusal, cancellation or failure is its own stage.
public sealed class DatabaseCreateStageMetricsTests
{
    private static readonly string[] Labels = ["outcome", "stage"];

    [Test]
    public async Task A_create_records_each_stage_completed_and_a_refused_create_ends_at_the_refusing_stage()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("create stages");
        var project = await api.CreateProjectAsync(organization, "create stages");
        var databases = ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases");
        using var stages = new Stages(api);

        using (var created = await api.Client.PostAsJsonAsync(databases, new { name = "main" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        await Assert.That(stages.Take()).IsEqualTo("serving:completed precheck:completed prepare:completed quota_check:completed register:completed complete:completed");

        using (var duplicate = await api.Client.PostAsJsonAsync(databases, new { name = "main" }))
        {
            await ErrorAssert.ErrorAsync(duplicate, HttpStatusCode.Conflict, "name_taken", "name is already taken");
        }

        await Assert.That(stages.Take()).IsEqualTo("serving:completed precheck:refused");
        await Assert.That(stages.Durations).IsEqualTo(8);
    }

    [Test]
    public async Task A_create_canceled_while_preparing_records_the_prepare_stage_canceled_and_its_cleanup_separately()
    {
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        await using var api = await ManagementApi.StartAsync(services: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            if (Volatile.Read(ref armed) && step == "create.intent")
            {
                cancellation.Cancel();
            }
        }));
        var organization = await api.CreateOrganizationAsync("canceled create");
        var project = await api.CreateProjectAsync(organization, "canceled create");
        using var stages = new Stages(api);
        Volatile.Write(ref armed, true);

        var management = api.Server.Services.GetRequiredService<IManagementService>();
        await Assert.That(async () => await management.CreateDatabaseAsync(api.User, organization, project, "main", cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(stages.Take()).IsEqualTo("serving:completed precheck:completed replicas:completed prepare:canceled cleanup:completed");
        // The cleanup the stage names ran: no intent, registration or file remains.
        await Assert.That(await PendingCleanupAsync(api)).IsEqualTo(0);
        await Assert.That(await ListedAsync(api, organization, project)).IsEqualTo(0);
        await Assert.That(DatabaseFiles(api, organization, project)).IsEqualTo(0);
    }

    [Test]
    public async Task A_create_failing_after_its_registration_commit_records_the_register_stage_faulted_and_keeps_the_database()
    {
        var armed = false;
        var injected = new InvalidOperationException("interrupted after registration commit");
        await using var api = await ManagementApi.StartAsync(services: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            if (Volatile.Read(ref armed) && step == "create.committed")
            {
                throw injected;
            }
        }));
        var organization = await api.CreateOrganizationAsync("faulted create");
        var project = await api.CreateProjectAsync(organization, "faulted create");
        using var stages = new Stages(api);
        Volatile.Write(ref armed, true);

        using (var failed = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name = "main" }))
        {
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        }

        await Assert.That(stages.Take()).IsEqualTo("serving:completed precheck:completed prepare:completed quota_check:completed register:faulted cleanup:completed");
        // Cleanup confirmed the committed registration, so the database and its file stay and no intent remains.
        await Assert.That(await PendingCleanupAsync(api)).IsEqualTo(0);
        await Assert.That(await ListedAsync(api, organization, project)).IsEqualTo(1);
        await Assert.That(DatabaseFiles(api, organization, project)).IsEqualTo(1);
    }

    private static async Task<int> PendingCleanupAsync(ManagementApi api)
    {
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await catalog.BeginTransactionAsync(write: false, CancellationToken.None);
        return (await transaction.ListAsync("pending_database_cleanup", null, 100, 1024 * 1024, CancellationToken.None)).Documents.Count;
    }

    private static async Task<int> ListedAsync(ManagementApi api, string organization, string project)
    {
        using var list = JsonDocument.Parse(await api.Client.GetStringAsync(ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases")));
        return list.RootElement.GetProperty("items").GetArrayLength();
    }

    // Database files only: an open database also has Turso -wal and -shm files.
    private static int DatabaseFiles(ManagementApi api, string organization, string project)
    {
        var directory = Path.Combine(api.Server.DataDirectory, organization, project);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.db").Count() : 0;
    }

    // The stage measurements of this test's own server, in recording order, as "stage:outcome".
    private sealed class Stages : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<string> _recorded = new();
        private int _durations;

        public Stages(ManagementApi api)
        {
            var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "terrabase.database_create.stage_duration" && ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, seconds, tags, _) =>
            {
                var labels = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    labels[tag.Key] = (string)tag.Value!;
                }

                // Only the two fixed labels, never an identifier.
                _recorded.Enqueue(labels.Keys.SequenceEqual(Labels) && seconds >= 0 ? $"{labels["stage"]}:{labels["outcome"]}" : "unexpected");
                Interlocked.Increment(ref _durations);
            });
            _listener.Start();
        }

        public int Durations => Volatile.Read(ref _durations);

        public string Take()
        {
            var taken = new List<string>();
            while (_recorded.TryDequeue(out var stage))
            {
                taken.Add(stage);
            }

            return string.Join(' ', taken);
        }

        public void Dispose() => _listener.Dispose();
    }
}
