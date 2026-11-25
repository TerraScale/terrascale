using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using DbUp.Engine;
using DbUp.ScriptProviders;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Storage.Schema;

public sealed record SchemaScriptChanged(string Script);

public readonly union SchemaUpgrade(AuthCompleted, AuthUnavailable, SchemaScriptChanged);

public sealed record SchemaJournalEntry(string ScriptName, string? Checksum);

internal sealed record SchemaJournalPresent;

internal sealed record SchemaJournalMissing;

internal readonly union SchemaJournalProbe(SchemaJournalPresent, SchemaJournalMissing, AuthUnavailable);

internal sealed record SchemaScript(string Name, string Contents, string Checksum);

public sealed class TerraBaseDbUpRunner(AuthConnectionFactory connections, TimeProvider clock, ILogger<TerraBaseDbUpRunner> logger)
{
    private const string ScriptPrefix = "schema/";

    private const string CreateJournal = "CREATE TABLE IF NOT EXISTS schema_versions (script_name TEXT NOT NULL PRIMARY KEY, applied TEXT NOT NULL, checksum TEXT)";

    private const string JournalColumns = "SELECT COUNT(*) FROM pragma_table_info('schema_versions')";

    private const string ChecksumColumn = "SELECT COUNT(*) FROM pragma_table_info('schema_versions') WHERE name = 'checksum'";

    private const string AddChecksumColumn = "ALTER TABLE schema_versions ADD COLUMN checksum TEXT";

    private const string ReadJournal = "SELECT script_name AS ScriptName, checksum AS Checksum FROM schema_versions";

    private const string RecordChecksum = "UPDATE schema_versions SET checksum = @checksum WHERE script_name = @name AND checksum IS NULL";

    private const string RecordScript = "INSERT INTO schema_versions (script_name, applied, checksum) VALUES (@name, @applied, @checksum)";

    private static readonly IReadOnlyList<SchemaScript> Scripts =
        [.. new EmbeddedScriptProvider(typeof(TerraBaseDbUpRunner).Assembly, name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal)).GetScripts(null)
            .OrderBy(script => script.Name, StringComparer.Ordinal)
            .Select(Checksummed)];

    public async Task<SchemaUpgrade> UpgradeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var journal = await EnsureJournalAsync(connection, cancellationToken) switch
        {
            AuthCompleted => await ChecksumColumnAsync(connection, cancellationToken),
            AuthUnavailable unavailable => unavailable,
            SchemaScriptChanged changed => changed,
        };
        var applied = journal switch
        {
            AuthCompleted => await ApplyPendingAsync(connection, cancellationToken),
            AuthUnavailable or SchemaScriptChanged => journal,
        };
        var recorded = applied switch
        {
            AuthCompleted => await BackfillAsync(connection, cancellationToken),
            AuthUnavailable or SchemaScriptChanged => applied,
        };
        return recorded switch
        {
            AuthCompleted => await VerifyAsync(connection, cancellationToken),
            AuthUnavailable or SchemaScriptChanged => recorded,
        };
    }

    public async Task<bool> CurrentAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await JournalAsync(connection, cancellationToken) is { } journal
            && Scripts.All(script => journal.TryGetValue(script.Name, out var checksum) && checksum == script.Checksum);
    }

    private async Task<SchemaUpgrade> EnsureJournalAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var probe = await ProbeJournalAsync(connection, cancellationToken);
        return probe switch
        {
            SchemaJournalPresent => new AuthCompleted(),
            SchemaJournalMissing => await ExecuteAsync(() => connection.ExecuteAsync(CreateJournal), "schema journal", cancellationToken),
            AuthUnavailable unavailable => unavailable,
        };
    }

    private async Task<SchemaJournalProbe> ProbeJournalAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.ExecuteScalarAsync<long>(JournalColumns) > 0 ? new SchemaJournalPresent() : new SchemaJournalMissing();
        }
        catch (TerraBaseException failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Schema journal could not be inspected ({failure.ErrorCode:@Code})");
            return new AuthUnavailable("The schema journal could not be inspected.");
        }
    }

    private async Task<SchemaUpgrade> ChecksumColumnAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (await HasChecksumColumnAsync(connection))
        {
            return new AuthCompleted();
        }

        var added = await ExecuteAsync(() => connection.ExecuteAsync(AddChecksumColumn), "schema journal checksum", cancellationToken);
        return added switch
        {
            AuthCompleted or SchemaScriptChanged => added,
            AuthUnavailable unavailable => await HasChecksumColumnAsync(connection) ? new AuthCompleted() : unavailable,
        };
    }

    private async Task<SchemaUpgrade> BackfillAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (await JournalAsync(connection, cancellationToken) is not { } journal)
        {
            return new AuthUnavailable("The schema journal could not be read.");
        }

        foreach (var script in Scripts.Where(script => journal.TryGetValue(script.Name, out var checksum) && checksum is null))
        {
            var backfilled = await ExecuteAsync(() => connection.ExecuteAsync(RecordChecksum, new { name = script.Name, checksum = script.Checksum }), script.Name, cancellationToken);
            var outcome = backfilled switch
            {
                AuthCompleted => (SchemaUpgrade?)null,
                AuthUnavailable or SchemaScriptChanged => backfilled,
            };
            if (outcome is { } refused)
            {
                return refused;
            }

            logger.ZLogInformation($"Schema script {script.Name:@Script} checksum is recorded");
        }

        return new AuthCompleted();
    }

    private async Task<SchemaUpgrade> VerifyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (await JournalAsync(connection, cancellationToken) is not { } journal)
        {
            return new AuthUnavailable("The schema journal could not be read.");
        }

        foreach (var script in Scripts)
        {
            if (!journal.TryGetValue(script.Name, out var checksum) || checksum is null)
            {
                return new AuthUnavailable($"Schema script {script.Name} is not recorded with its checksum.");
            }

            if (checksum != script.Checksum)
            {
                logger.ZLogError($"Schema script {script.Name:@Script} changed after it was applied");
                return new SchemaScriptChanged(script.Name);
            }
        }

        return new AuthCompleted();
    }

    private async Task<SchemaUpgrade> ApplyPendingAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (await JournalAsync(connection, cancellationToken) is not { } applied)
        {
            return new AuthUnavailable("The schema journal could not be read.");
        }

        foreach (var script in Scripts.Where(script => !applied.ContainsKey(script.Name)))
        {
            var sql = script.Contents.TrimEnd().TrimEnd(';') + ";\n" + RecordScript;
            var appliedScript = await ExecuteAsync(() => connection.ExecuteAsync(sql, new { name = script.Name, applied = clock.GetUtcNow(), checksum = script.Checksum }),
                script.Name, cancellationToken);
            var refused = appliedScript switch
            {
                AuthCompleted => (SchemaUpgrade?)null,
                AuthUnavailable or SchemaScriptChanged => appliedScript,
            };
            if (refused is { } blocked
                && (await JournalAsync(connection, cancellationToken))?.ContainsKey(script.Name) != true)
            {
                return blocked;
            }

            logger.ZLogInformation($"Schema script {script.Name:@Script} is applied");
        }

        return new AuthCompleted();
    }

    private async Task<SchemaUpgrade> ExecuteAsync(Func<Task<int>> statement, string step, CancellationToken cancellationToken)
    {
        try
        {
            await statement();
            return new AuthCompleted();
        }
        catch (TerraBaseException failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Schema step {step:@Step} was refused ({failure.ErrorCode:@Code})");
            return new AuthUnavailable(failure.ErrorCode ?? "schema_refused");
        }
    }

    private static async Task<bool> HasChecksumColumnAsync(DbConnection connection) =>
        await connection.ExecuteScalarAsync<long>(ChecksumColumn) > 0;

    private async Task<Dictionary<string, string?>?> JournalAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await connection.QueryAsync<SchemaJournalEntry>(ReadJournal);
            return entries.ToDictionary(entry => entry.ScriptName, entry => entry.Checksum, StringComparer.Ordinal);
        }
        catch (TerraBaseException failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Schema journal could not be read ({failure.ErrorCode:@Code})");
            return null;
        }
    }

    private static SchemaScript Checksummed(SqlScript script) =>
        new(script.Name, script.Contents, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script.Contents))));
}
