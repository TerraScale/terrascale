using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Turso.Data.Sqlite;

namespace TerraScale.Dashboard.E2E.Demo
{
    internal sealed record TestSqlStatement(string Sql, IReadOnlyList<string?> Args);

    internal sealed record TestSqlResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, long RowsAffected);

    internal sealed class TestSqlFailure(int statement, string code, string sqlstate, string message) : Exception(message)
    {
        public int Statement { get; } = statement;
        public string Code { get; } = code;
        public string Sqlstate { get; } = sqlstate;
    }

    internal sealed class TestSqlEngine : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("ts-dashboard-state-").FullName;
        private readonly SqliteConnection connection;
        private readonly SemaphoreSlim gate = new(1, 1);

        public TestSqlEngine()
        {
            connection = new SqliteConnection($"Data Source={Path.Combine(directory, "ts-dashboard.db")};Pooling=False");
            connection.Open();
        }

        public async Task<IReadOnlyList<TestSqlResult>> ExecuteAsync(IReadOnlyList<TestSqlStatement> statements, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                using var transaction = connection.BeginTransaction();
                var results = new List<TestSqlResult>(statements.Count);
                for (var index = 0; index < statements.Count; index++)
                {
                    try { results.Add(Run(transaction, statements[index])); }
                    catch (SqliteException error)
                    {
                        transaction.Rollback();
                        var (code, sqlstate) = Code(error);
                        throw new TestSqlFailure(index, code, sqlstate, error.Message);
                    }
                }

                transaction.Commit();
                return results;
            }
            finally { gate.Release(); }
        }

        public async Task<IReadOnlyList<(string Name, int Rows)>> TablesAsync()
        {
            var names = (await ExecuteAsync([new("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name", [])], default))[0].Rows
                .Select(row => (string)row[0]!).ToArray();
            var counts = await ExecuteAsync([.. names.Select(name => new TestSqlStatement($"SELECT count(*) FROM {name}", []))], default);
            return [.. names.Select((name, index) => (name, Convert.ToInt32(counts[index].Rows[0][0], System.Globalization.CultureInfo.InvariantCulture)))];
        }

        public async Task<string> DigestAsync()
        {
            var text = new StringBuilder();
            foreach (var (name, _) in await TablesAsync())
            {
                foreach (var row in (await ExecuteAsync([new($"SELECT id, version, body FROM {name} ORDER BY id", [])], default))[0].Rows)
                {
                    text.Append(name).Append('/').AppendJoin(':', row).Append('\n');
                }
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }

        public void Dispose()
        {
            connection.Dispose();
            gate.Dispose();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }

        private TestSqlResult Run(SqliteTransaction transaction, TestSqlStatement statement)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var index = 0;
            command.CommandText = string.Concat(statement.Sql.Select(character => character == '?' ? "@p" + (++index).ToString(System.Globalization.CultureInfo.InvariantCulture) : character.ToString()));
            for (var position = 0; position < statement.Args.Count; position++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@p" + (position + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameter.Value = (object?)statement.Args[position] ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            using var reader = command.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var rows = new List<IReadOnlyList<object?>>();
            while (reader.Read())
            {
                rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index))]);
            }

            return new(columns, rows, Math.Max(0, reader.RecordsAffected));
        }

        private static (string Code, string Sqlstate) Code(SqliteException error)
        {
            return error.Message switch
            {
                var message when message.Contains("UNIQUE constraint failed", StringComparison.Ordinal) => ("unique_violation", "23505"),
                var message when message.Contains("NOT NULL constraint failed", StringComparison.Ordinal) => ("not_null_violation", "23502"),
                var message when message.Contains("constraint failed", StringComparison.Ordinal) => ("constraint_violation", "23000"),
                _ when error.SqliteErrorCode == 5 => ("serialization_conflict", "40001"),
                _ => ("sql_error", "0A000"),
            };
        }

        public static JsonElement Body(IReadOnlyList<TestSqlResult> results)
        {
            return JsonSerializer.SerializeToElement(new
            {
                results = results.Select(result => new
                {
                    columns = result.Columns.Select(name => new { name, decltype = (string?)null }),
                    rows = result.Rows,
                    rows_affected = result.RowsAffected,
                    last_insert_rowid = (long?)null
                })
            });
        }
    }
}
