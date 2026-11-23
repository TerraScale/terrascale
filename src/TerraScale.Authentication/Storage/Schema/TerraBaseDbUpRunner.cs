using System.Data.Common;
using Dapper;
using DbUp.Engine;
using DbUp.ScriptProviders;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Storage.Schema;

public sealed class TerraBaseDbUpRunner(AuthConnectionFactory connections, TimeProvider clock, ILogger<TerraBaseDbUpRunner> logger)
{
    private const string ScriptPrefix = "schema/";

    private static readonly IReadOnlyList<SqlScript> Scripts =
        [.. new EmbeddedScriptProvider(typeof(TerraBaseDbUpRunner).Assembly, name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal)).GetScripts(null)
            .OrderBy(script => script.Name, StringComparer.Ordinal)];

    public async Task<Result<Done, Unavailable>> UpgradeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await AppliedAsync(connection, cancellationToken) is not { } applied)
        {
            return new Unavailable("The schema journal could not be read.");
        }

        foreach (var script in Scripts.Where(script => !applied.Contains(script.Name)))
        {
            if (await ApplyAsync(connection, script, cancellationToken) is Unavailable failure
                && (await AppliedAsync(connection, cancellationToken))?.Contains(script.Name) != true)
            {
                return failure;
            }

            logger.ZLogInformation($"Schema script {script.Name:@Script} is applied");
        }

        return new Done();
    }

    public async Task<bool> CurrentAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await AppliedAsync(connection, cancellationToken) is { } applied && Scripts.All(script => applied.Contains(script.Name));
    }

    private async Task<Result<Done, Unavailable>> ApplyAsync(DbConnection connection, SqlScript script, CancellationToken cancellationToken)
    {
        var sql = script.Contents.TrimEnd().TrimEnd(';') + ";\nINSERT INTO schema_versions (script_name, applied) VALUES (@name, @applied)";
        try
        {
            await connection.ExecuteAsync(sql, new { name = script.Name, applied = clock.GetUtcNow() });
            return new Done();
        }
        catch (TerraBaseException failure)
        {
            logger.ZLogWarning($"Schema script {script.Name:@Script} was refused ({failure.ErrorCode:@Code})");
            return new Unavailable(failure.ErrorCode ?? "schema_refused");
        }
    }

    private static async Task<HashSet<string>?> AppliedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var names = await connection.QueryAsync<string>(
                "CREATE TABLE IF NOT EXISTS schema_versions (script_name TEXT NOT NULL PRIMARY KEY, applied TEXT NOT NULL);\nSELECT script_name FROM schema_versions");
            return [.. names];
        }
        catch (TerraBaseException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
