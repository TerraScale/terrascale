using System.Diagnostics.Metrics;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    // Up to the 60-second runner deadline that canceled a live create (docs/terrabase/database-create-stage-metrics.md).
    private static readonly double[] CreateStageBuckets = [0.001, 0.005, 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 15, 20, 25, 30, 45, 60];

    private readonly Histogram<double> _createStages = meters.Create(Telemetry.MeterName).CreateHistogram<double>(
        "terrabase.database_create.stage_duration", "s", "Time one database creation spent in each stage, by stage and how it ended.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = CreateStageBuckets });

    private async ValueTask<AddDatabaseResult> AddDatabaseAsync(
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string name, string? sourceId, string? branchedFrom, CancellationToken cancellationToken,
        string? id = null, DatabasePlacement? placement = null, DateTimeOffset? createdAt = null)
    {
        var stages = new CreateStages(_createStages, time);
        try
        {
            var result = await AddDatabaseInStagesAsync(stages, project, name, sourceId, branchedFrom, id, placement, createdAt, cancellationToken).ConfigureAwait(false);
            stages.End(result is AddedDatabase ? CreateStages.Completed : CreateStages.Refused);
            return result;
        }
        catch (Exception exception)
        {
            stages.End(CreateStages.Outcome(exception));
            throw;
        }
    }

    // One database creation's current stage. A stage records once, with how it ended, when the next starts or the creation
    // ends; the stage and outcome names are these constants, so the label set stays bounded.
    private sealed class CreateStages(Histogram<double> histogram, TimeProvider time)
    {
        public const string Serving = "serving";
        public const string Precheck = "precheck";
        public const string QuotaEnter = "quota_enter";
        public const string Replicas = "replicas";
        public const string Prepare = "prepare";
        public const string QuotaCheck = "quota_check";
        public const string Register = "register";
        public const string Cleanup = "cleanup";
        public const string Complete = "complete";

        public const string Completed = "completed";
        public const string Refused = "refused";
        public const string Canceled = "canceled";
        public const string Faulted = "faulted";

        private string? _stage;
        private long _started;

        // How a stage ended by this exception. A capacity refusal is answered as unavailable, not raised to the caller.
        public static string Outcome(Exception exception) => exception switch
        {
            OperationCanceledException => Canceled,
            QueryCapacityException => Refused,
            _ => Faulted,
        };

        public void Next(string stage, string outcome = Completed)
        {
            End(outcome);
            (_stage, _started) = (stage, time.GetTimestamp());
        }

        public void End(string outcome)
        {
            if (_stage is { } stage)
            {
                histogram.Record(time.GetElapsedTime(_started).TotalSeconds,
                    new KeyValuePair<string, object?>("stage", stage), new KeyValuePair<string, object?>("outcome", outcome));
                _stage = null;
            }
        }
    }
}
