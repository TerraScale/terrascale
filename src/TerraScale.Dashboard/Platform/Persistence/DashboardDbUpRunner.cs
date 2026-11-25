using System.Data;
using System.Security.Cryptography;
using System.Text;
using DbUp.Engine;
using DbUp.ScriptProviders;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class DashboardDbUpRunner(IDashboardIdentity identity, DashboardDb storage, TimeProvider time, ILogger<DashboardDbUpRunner> logger)
    : BackgroundService, IHostedLifecycleService
{
    private static readonly Dictionary<string, object?> NoParameters = [];

    private const string SchemaPrefix = "TerraScale.Dashboard.Schema.";
    private const string Journal = "CREATE TABLE IF NOT EXISTS schema_versions (script_name TEXT NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL)";
    private const string ChecksumJournal = "CREATE TABLE IF NOT EXISTS schema_checksums (script_name TEXT NOT NULL PRIMARY KEY, sha256 TEXT NOT NULL)";
    private const string Applied = "SELECT script_name FROM schema_versions";
    private const string AppliedChecksums =
        "SELECT v.script_name AS Name, c.sha256 AS Sha256 FROM schema_versions v LEFT JOIN schema_checksums c ON c.script_name = v.script_name";
    private const string Record = "INSERT INTO schema_versions (script_name, applied_at) VALUES (@Name, @AppliedAt)";
    private const string RecordChecksum = "INSERT INTO schema_checksums (script_name, sha256) VALUES (@Name, @Sha256)";
    private const string BackfillChecksum = "INSERT INTO schema_checksums (script_name, sha256) VALUES (@Name, @Sha256) ON CONFLICT(script_name) DO NOTHING";
    private const string RecordedChecksum = "SELECT sha256 FROM schema_checksums WHERE script_name = @Name";
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupAttempt = TimeSpan.FromSeconds(10);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Initialized => _ready.Task.IsCompleted;

    public Task WhenInitialized => _ready.Task;

    public static IReadOnlyList<SqlScript> Scripts() =>
        [.. new EmbeddedScriptProvider(typeof(DashboardDbUpRunner).Assembly, static name => name.StartsWith(SchemaPrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .GetScripts(null)
            .OrderBy(static script => script.SqlScriptOptions.RunGroupOrder)
            .ThenBy(static script => script.Name, StringComparer.Ordinal)];

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        await identity.ReadAsync(cancellationToken);
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            attempt.CancelAfter(StartupAttempt);
            try
            {
                await UpgradeAsync(attempt.Token);
                _ready.TrySetResult();
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.DashboardStateIsNotInitializedAt(StorageFailure.Describe(error));
            }
        }

        await base.StartAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> PendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        var applied = (await DashboardDb.RowsAsync(connection, Applied, NoParameters, static reader => reader.GetString(0), null, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        return [.. Scripts().Select(static script => script.Name).Where(name => !applied.Contains(name))];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Initialized)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Task.Delay(Backoff(attempt - 1, Random.Shared.NextDouble()), time, stoppingToken);
                await UpgradeAsync(stoppingToken);
                _ready.TrySetResult();
                logger.DashboardStateInitializedAfterAttempts(attempt + 1);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.DashboardStateIsNotInitializedAfter(attempt + 1, StorageFailure.Describe(error));
            }
        }
    }

    private static TimeSpan Backoff(int attempt, double jitter) =>
        TimeSpan.FromTicks((long)(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Min(attempt, 10)) * (0.5 + 0.5 * Math.Clamp(jitter, 0, 1))));

    public async Task UpgradeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        await DashboardDb.RunAsync(connection, Journal, NoParameters, null, cancellationToken);
        await DashboardDb.RunAsync(connection, ChecksumJournal, NoParameters, null, cancellationToken);
        var scripts = Scripts().ToDictionary(static script => script.Name, static script => Checksum(script.Contents), StringComparer.Ordinal);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        var rows = await DashboardDb.RowsAsync(connection, AppliedChecksums, NoParameters,
            static reader => new AppliedScriptRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)), null, cancellationToken);
        foreach (var row in rows)
        {
            applied.Add(row.Name);
            if (!scripts.TryGetValue(row.Name, out var expected))
            {
                continue;
            }

            var recorded = row.Sha256;
            if (recorded is null)
            {
                await DashboardDb.RunAsync(connection, BackfillChecksum, new Dictionary<string, object?> { ["Name"] = row.Name, ["Sha256"] = expected }, null, cancellationToken);
                recorded = (await DashboardDb.RowsAsync(connection, RecordedChecksum, new Dictionary<string, object?> { ["Name"] = row.Name },
                    static reader => reader.GetString(0), null, cancellationToken)).Single();
                logger.DashboardSchemaChecksumRecorded(row.Name);
            }

            if (!string.Equals(recorded, expected, StringComparison.Ordinal))
            {
                logger.DashboardSchemaChecksumMismatch(row.Name, recorded, expected);
                throw new InvalidDataException($"Applied dashboard schema script {row.Name} no longer matches its released checksum.");
            }
        }

        foreach (var script in Scripts().Where(script => !applied.Contains(script.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await DashboardDb.RunAsync(connection, script.Contents, NoParameters, transaction, cancellationToken);
            await DashboardDb.RunAsync(connection, Record, new Dictionary<string, object?> { ["Name"] = script.Name, ["AppliedAt"] = time.GetUtcNow().UtcDateTime }, transaction, cancellationToken);
            await DashboardDb.RunAsync(connection, RecordChecksum, new Dictionary<string, object?> { ["Name"] = script.Name, ["Sha256"] = scripts[script.Name] }, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.DashboardSchemaScriptApplied(script.Name);
        }
    }

    private static string Checksum(string contents) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));
}

internal sealed record AppliedScriptRow(string Name, string? Sha256);
