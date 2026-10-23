using System.Text;
using TerraBase.Engine.Sqlite;
using Turso.Data.Sqlite;

namespace TerraBase.E2E
{
    /// <summary>
    /// The raw driver as an application would use it with TerraBase's durability: one writer connection (TerraBase has one
    /// per database), each write its own BEGIN IMMEDIATE upsert and COMMIT with synchronous FULL (the WAL is synced before
    /// the commit returns), Turso's default WAL journal, and a connection per concurrent reader. Same table shape as a
    /// TerraBase collection: (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL).
    /// </summary>
    internal sealed class TursoTarget : ITarget
    {
        private readonly string _connectionString;
        private readonly SqliteConnection _writer;
        private readonly SqliteCommand _upsert;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly System.Collections.Concurrent.ConcurrentBag<(SqliteConnection, SqliteCommand)> _readers = [];

        public TursoTarget(string file, bool capture)
        {
            _connectionString = $"Data Source={file}";
            _writer = new SqliteConnection(_connectionString);
            _writer.Open();
            Execute(_writer, "PRAGMA synchronous = FULL;");
            Execute(_writer, "CREATE TABLE docs (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);");
            if (capture)
            {
                Execute(_writer, SqliteSession.CaptureOn);
            }

            _upsert = _writer.CreateCommand();
            _upsert.CommandText = "INSERT INTO docs (id, doc) VALUES ($id, $doc) ON CONFLICT (id) DO UPDATE SET doc = excluded.doc;";
            _upsert.Parameters.AddWithValue("$id", string.Empty);
            _upsert.Parameters.AddWithValue("$doc", string.Empty);
        }

        public async Task PutAsync(string id, byte[] json)
        {
            await _gate.WaitAsync();
            try
            {
                using var transaction = _writer.BeginTransaction(deferred: false);
                _upsert.Transaction = transaction;
                _upsert.Parameters["$id"].Value = id;
                _upsert.Parameters["$doc"].Value = Encoding.UTF8.GetString(json);
                _upsert.ExecuteNonQuery();
                transaction.Commit();
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            using var transaction = _writer.BeginTransaction(deferred: false);
            _upsert.Transaction = transaction;
            foreach (var (id, json) in documents)
            {
                _upsert.Parameters["$id"].Value = id;
                _upsert.Parameters["$doc"].Value = Encoding.UTF8.GetString(json);
                _upsert.ExecuteNonQuery();
            }

            transaction.Commit();
            return Task.CompletedTask;
        }

        public Task GetAsync(string id)
        {
            _ = Read(id) ?? throw new InvalidOperationException($"turso: {id} is missing");
            return Task.CompletedTask;
        }

        /// <summary>The stored document, on a pooled reader connection; null when absent.</summary>
        public string? Read(string id)
        {
            if (!_readers.TryTake(out var reader))
            {
                var connection = new SqliteConnection(_connectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT doc FROM docs WHERE id = $id;";
                command.Parameters.AddWithValue("$id", string.Empty);
                reader = (connection, command);
            }

            try
            {
                reader.Item2.Parameters["$id"].Value = id;
                return reader.Item2.ExecuteScalar() as string;
            }
            finally
            {
                _readers.Add(reader);
            }
        }

        public ValueTask DisposeAsync()
        {
            _upsert.Dispose();
            _writer.Dispose();
            foreach (var (connection, command) in _readers)
            {
                command.Dispose();
                connection.Dispose();
            }

            _gate.Dispose();
            return ValueTask.CompletedTask;
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
