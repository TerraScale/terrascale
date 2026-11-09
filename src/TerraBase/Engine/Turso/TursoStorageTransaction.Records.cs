using System.Buffers;
using System.Text;
using TerraBase.Features.Qos;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    // Kinds this write transaction found or created; forgotten when it drops one or rolls back to a savepoint.
    private HashSet<string>? _kinds;

    public ValueTask<StoredRecord?> GetAsync(string kind, string id, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            RequireSystem();
            var command = Command(TursoStatements.Get, kind);
            command.Parameters.AddWithValue("$id", id);
            CountRead("point");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            ReserveJson(reader.GetInt64(2));
            return new StoredRecord(id, reader.GetInt64(0), TursoSession.Bytes(reader, 1));
        }, cancellationToken);

    public ValueTask<long> GetStoredBytesAsync(CancellationToken cancellationToken) =>
        RunAsync(() => (long)Command(store.IsSystem ? TursoStatements.StoredRecordBytes : TursoStatements.SqlStorageBytes).ExecuteScalar()!,
            cancellationToken);

    public ValueTask<long?> GetVersionAsync(string kind, string id, CancellationToken cancellationToken) =>
        RunAsync(() => ReadVersion(kind, id), cancellationToken);

    private long? ReadVersion(string kind, string id)
    {
        RequireSystem();
        var command = Command(TursoStatements.Version, kind);
        command.Parameters.AddWithValue("$id", id);
        CountRead("point");
        return command.ExecuteScalar() as long?;
    }

    public ValueTask<long> PutAsync(string kind, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            RequireSystem();
            ThrowIfReadOnly();
            ArgumentException.ThrowIfNullOrEmpty(id);
            var body = StrictJson.Read(json, requireObject: false);
            if (_kinds?.Contains(kind) != true)
            {
                if (Command(TursoStatements.GetKind, kind).ExecuteScalar() is null)
                {
                    var create = Command(TursoStatements.CreateKind, kind);
                    create.Parameters.AddWithValue("$created", store.Time.GetUtcNow().ToUnixTimeMilliseconds());
                    create.Parameters.AddWithValue("$entityId", EntityIds.New().ToString());
                    create.ExecuteNonQuery();
                    ChangedReaderState = true;
                }

                (_kinds ??= [with(StringComparer.Ordinal)]).Add(kind);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var version = Offset();
            var command = Command(TursoStatements.Put, kind);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$body", body);
            command.ExecuteNonQuery();
            return version;
        }, cancellationToken, mutation: true);

    public ValueTask<bool> DeleteAsync(string kind, string id, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (ReadVersion(kind, id) is null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = Offset();
            var command = Command(TursoStatements.Delete, kind);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            return true;
        }, cancellationToken, mutation: true);

    public ValueTask<RecordPage> ListAsync(string kind, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget(() =>
        {
            RequireSystem();
            var command = Command(afterId is null ? TursoStatements.ListFirst : TursoStatements.ListAfter, kind);
            if (afterId is not null)
            {
                command.Parameters.AddWithValue("$after", afterId);
            }

            command.Parameters.AddWithValue("$limit", limit + 1);
            var records = new List<StoredRecord>();
            long bytes = 0;
            CountRead("query");
            using var reader = command.ExecuteReader();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read())
                {
                    break;
                }

                var size = reader.GetInt64(3);
                if (records.Count == limit || (records.Count > 0 && bytes + size > maxBytes))
                {
                    return new RecordPage(records, HasMore: true);
                }

                bytes += size;
                ReserveJson(size);
                records.Add(new StoredRecord(reader.GetString(0), reader.GetInt64(1), TursoSession.Bytes(reader, 2)));
            }

            return new RecordPage(records, HasMore: false);
        }, cancellationToken), cancellationToken);

    public ValueTask<(IReadOnlyList<RecordKind> Kinds, bool HasMore)> ListKindsAsync(string? afterName, int limit, CancellationToken cancellationToken) =>
        RunAsync<(IReadOnlyList<RecordKind>, bool)>(() =>
        {
            RequireSystem();
            var command = Command(afterName is null ? TursoStatements.ListKindsFirst : TursoStatements.ListKindsAfter);
            if (afterName is not null)
            {
                command.Parameters.AddWithValue("$after", afterName);
            }

            command.Parameters.AddWithValue("$limit", limit + 1);
            var kinds = new List<RecordKind>();
            using var reader = command.ExecuteReader();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read())
                {
                    break;
                }

                if (kinds.Count == limit)
                {
                    return (kinds, true);
                }

                kinds.Add(new RecordKind(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }

            return (kinds, false);
        }, cancellationToken);

    public ValueTask<bool> DropKindAsync(string name, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            RequireSystem();
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            var records = Command(TursoStatements.DropRecords, name).ExecuteNonQuery();
            _kinds = null;
            if (Command(TursoStatements.DropKind, name).ExecuteNonQuery() == 0 && records == 0)
            {
                return false;
            }

            _ = Offset();
            ChangedReaderState = true;
            return true;
        }, cancellationToken, mutation: true);

    public ValueTask<long> DeleteKindsAsync(string prefix, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ArgumentException.ThrowIfNullOrEmpty(prefix);
            RequireSystem();
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            var through = PrefixSuccessor(prefix);
            var records = Command(through is null ? TursoStatements.DeletePrefixRecordsFrom : TursoStatements.DeletePrefixRecords);
            var kinds = Command(through is null ? TursoStatements.DeletePrefixKindsFrom : TursoStatements.DeletePrefixKinds);
            foreach (var command in new[] { records, kinds })
            {
                command.Parameters.AddWithValue("$from", prefix);
                if (through is not null)
                {
                    command.Parameters.AddWithValue("$to", through);
                }
            }

            long deleted = records.ExecuteNonQuery();
            _kinds = null;
            if (kinds.ExecuteNonQuery() > 0 || deleted > 0)
            {
                _ = Offset();
                ChangedReaderState = true;
            }

            return deleted;
        }, cancellationToken, mutation: true);

    private void RequireSystem()
    {
        if (!store.IsSystem)
        {
            throw new InvalidOperationException("records live in the system database only");
        }
    }

    private static string? PrefixSuccessor(string name)
    {
        var length = name.Length;
        while (length > 0)
        {
            if (Rune.DecodeLastFromUtf16(name.AsSpan(0, length), out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new ArgumentException("Kind prefixes must contain valid Unicode scalar values.", nameof(name));
            }

            length -= consumed;
            if (rune.Value < 0x10ffff)
            {
                var next = rune.Value == 0xd7ff ? 0xe000 : rune.Value + 1;
                return name[..length] + new Rune(next).ToString();
            }
        }

        return null;
    }

    private static void ReserveJson(long size) =>
        QosContext.ReserveResult(checked(size * 3 + 2048), null);
}
