using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUlid;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public sealed class TerraBaseDashboardState(TerraBaseRpcClient rpc) : IDashboardState
    {
        private const int ListPageSize = 200;
        private static readonly HashSet<string> Tables = [.. DashboardStateSchema.Tables];

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await ExecuteAsync([.. DashboardStateSchema.Statements.Select(sql => new StateStatement(sql))], cancellationToken);
            await CheckReadyAsync(cancellationToken);
        }

        public async Task CheckReadyAsync(CancellationToken cancellationToken)
        {
            var result = (await ExecuteAsync([new("SELECT name FROM sqlite_master WHERE type = 'table'")], cancellationToken))[0];
            var present = result.Rows.Select(row => row[0].GetString()).ToHashSet(StringComparer.Ordinal);
            if (DashboardStateSchema.Tables.Any(table => !present.Contains(table)))
            {
                throw new IOException("Required dashboard state tables are unavailable.");
            }
        }

        public async Task<StateRecord?> GetAsync(string table, string id, CancellationToken cancellationToken)
        {
            var result = (await ExecuteAsync([new($"SELECT id, version, body FROM {Table(table)} WHERE id = ?", [id])], cancellationToken))[0];
            return result.Rows.Count switch
            {
                0 => null,
                1 => Record(result.Rows[0]),
                _ => throw new IOException("Dashboard state returned duplicate rows."),
            };
        }

        public async Task<IReadOnlyList<StateRecord>> ListAsync(string table, CancellationToken cancellationToken)
        {
            var records = new List<StateRecord>();
            string? cursor = null;
            do
            {
                var page = await PageAsync(table, new Dictionary<string, string>(), null, ListPageSize, cursor, cancellationToken);
                records.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor is not null);
            return records;
        }

        public async Task<StatePage> PageAsync(string table, IReadOnlyDictionary<string, string> scope, IReadOnlyList<string>? fields, int limit, string? cursor,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
            var sql = new StringBuilder("SELECT id, version, ");
            sql.Append(fields is null ? "body" : "json_object(" + string.Join(", ", fields.Select(field => $"'{Field(field)}', json_extract(body, '$.{Field(field)}')")) + ")");
            sql.Append(" FROM ").Append(Table(table)).Append(" WHERE id > ?");
            var args = new List<string?> { cursor ?? "" };
            foreach (var (field, value) in scope)
            {
                sql.Append(" AND json_extract(body, '$.").Append(Field(field)).Append("') = ?");
                args.Add(value);
            }

            sql.Append(" ORDER BY id LIMIT ").Append((limit + 1).ToString(CultureInfo.InvariantCulture));
            var rows = (await ExecuteAsync([new(sql.ToString(), args)], cancellationToken))[0].Rows;
            var items = rows.Take(limit).Select(Record).ToArray();
            return new(items, rows.Count > limit ? items[^1].Id : null);
        }

        public async Task PutAsync(string table, string id, JsonElement body, StatePrecondition precondition, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(precondition);
            var version = Ulid.NewUlid().ToString();
            if (precondition.Version is not { } expected)
            {
                try
                {
                    await ExecuteAsync([new($"INSERT INTO {Table(table)} (id, version, body) VALUES (?, ?, ?)", [id, version, body.GetRawText()])], cancellationToken);
                }
                catch (TerraBaseException error) when (error.Fault.SqliteCode?.StartsWith("SQLITE_CONSTRAINT", StringComparison.Ordinal) == true)
                {
                    throw new StateConflictException();
                }

                return;
            }

            var updated = await ExecuteAsync([new($"UPDATE {Table(table)} SET version = ?, body = ? WHERE id = ? AND version = ?", [version, body.GetRawText(), id, expected])],
                cancellationToken);
            if (updated[0].RowsAffected != 1)
            {
                throw new StateConflictException();
            }
        }

        public async Task DeleteAsync(string table, string id, string version, CancellationToken cancellationToken)
        {
            var deleted = await ExecuteAsync([new($"DELETE FROM {Table(table)} WHERE id = ? AND version = ?", [id, version])], cancellationToken);
            if (deleted[0].RowsAffected != 1)
            {
                throw new StateConflictException();
            }
        }

        private async Task<IReadOnlyList<StateResult>> ExecuteAsync(IReadOnlyList<StateStatement> statements, CancellationToken cancellationToken)
        {
            var answer = await rpc.Platform<IManagementService>().WithCancellationToken(cancellationToken).ExecuteSqlAsync(DashboardStateSchema.ProjectId,
                DashboardStateSchema.Database, JsonSerializer.SerializeToElement(new StateRequest(statements), StateJson.Default.StateRequest));
            var results = answer.Deserialize(StateJson.Default.StateResponse)?.Results;
            if (results is null || results.Count != statements.Count || results.Any(result => result is null || result.Rows is null || result.Rows.Any(row => row is null)))
            {
                throw new IOException("TerraBase returned an invalid dashboard state result.");
            }

            return results;
        }

        private static StateRecord Record(IReadOnlyList<JsonElement> row)
        {
            if (row.Count != 3 || row[0].GetString() is not { Length: > 0 } id || row[1].GetString() is not { Length: > 0 } version || row[2].GetString() is not { } body)
            {
                throw new IOException("Dashboard state row is invalid.");
            }

            using var json = JsonDocument.Parse(body);
            return new(id, version, json.RootElement.Clone());
        }

        private static string Table(string table)
        {
            return Tables.Contains(table) ? table : throw new ArgumentException("Unknown dashboard state table.", nameof(table));
        }

        private static string Field(string field)
        {
            return field.Length is > 0 and <= 64 && field.All(char.IsAsciiLetterOrDigit) ? field : throw new ArgumentException("Invalid dashboard state field.", nameof(field));
        }
    }

    internal sealed record StateStatement(string Sql, IReadOnlyList<string?>? Args = null);

    internal sealed record StateRequest(IReadOnlyList<StateStatement> Statements);

    internal sealed record StateResponse(IReadOnlyList<StateResult>? Results);

    internal sealed record StateResult(IReadOnlyList<IReadOnlyList<JsonElement>> Rows, [property: JsonPropertyName("rows_affected")] long RowsAffected);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StateRequest))]
    [JsonSerializable(typeof(StateResponse))]
    internal sealed partial class StateJson : JsonSerializerContext;
}
