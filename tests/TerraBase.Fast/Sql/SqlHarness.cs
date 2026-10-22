using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Features.Sql;

namespace TerraBase.Fast.Sql;

/// <summary>Runs statements through the server's <see cref="ISqlService"/> and reads what they stored.</summary>
internal static class SqlHarness
{
    /// <summary>Runs <paramref name="sql"/> in order on one session of database <c>db</c>, stopping at the first error.</summary>
    public static async Task<List<SqlOutcome>> RunAsync(DocumentApi api, bool canWrite, params string[] sql)
    {
        var session = api.Server.Services.GetRequiredService<ISqlService>().Open(await api.TenantOfAsync(), canWrite);
        await using (session)
        {
            var outcomes = new List<SqlOutcome>();
            foreach (var text in sql)
            {
                var outcome = await session.ExecuteAsync(new SqlCommand(text, [], new Dictionary<string, SqlValue>()), CancellationToken.None);
                outcomes.Add(outcome);
                if (outcome is SqlError)
                {
                    break;
                }
            }

            await session.EndRequestAsync();
            return outcomes;
        }
    }

    /// <summary>Runs one statement with write access.</summary>
    public static async Task<SqlOutcome> RunAsync(DocumentApi api, string sql) => (await RunAsync(api, canWrite: true, sql))[0];

    /// <summary>The SQLite code of a failed outcome, or null for a result.</summary>
    public static string? Code(SqlOutcome outcome) => outcome switch
    {
        SqlError error => error.Code,
        _ => null,
    };

    /// <summary>The stored definition (member <c>sql</c> of the collection's metadata), or null.</summary>
    public static async Task<JsonElement?> DefinitionAsync(DocumentApi api, string table)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await using var transaction = (await engine.BeginTransactionAsync(await api.TenantOfAsync(), write: false, CancellationToken.None))!;
        return await transaction.GetCollectionTableAsync(table, CancellationToken.None);
    }
}
