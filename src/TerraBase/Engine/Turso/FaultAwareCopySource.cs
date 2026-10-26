using System.Collections;
using System.Data;
using System.Data.Common;
using Turso;
using Turso.Raw.Public;

namespace TerraBase.Engine.Turso;

/// <summary>Marks damage at the synchronous source boundaries of <see cref="TursoCopy"/>, without observing target writes.</summary>
internal sealed class FaultAwareCopySource(string connectionString, Action<Exception> damaged) : TursoConnection(connectionString)
{
    public override void Open() => Observe(base.Open);

    public override void Close() => Observe(base.Close);

    protected override DbCommand CreateDbCommand() => new SourceCommand(this);

    private T Observe<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception error) when (NoteDamage(error))
        {
            throw;
        }
    }

    private void Observe(Action call) => Observe(() =>
    {
        call();
        return true;
    });

    // The compatibility provider's SqliteCommand.GetNativeSqliteErrorCode maps these actual local statuses to
    // SQLITE_NOTADB, SQLITE_CORRUPT and SQLITE_IOERR. Observe them without translating or replacing the native exception.
    private bool NoteDamage(Exception error)
    {
        if (error is TursoNativeException { StatusCode: 132 or 133 or 134 } || TursoStorageTransaction.Damaged(error))
        {
            damaged(error);
        }

        return false;
    }

    internal static void Abort(DbDataReader reader)
    {
        if (reader is SourceReader wrapped)
        {
            wrapped.Abort();
        }
        else
        {
            ((TursoDataReader)reader).Abort();
        }
    }

    private sealed class SourceCommand : TursoCommand
    {
        private readonly FaultAwareCopySource _source;

        internal SourceCommand(FaultAwareCopySource source) : base(source)
        {
            _source = source;
        }

        public override int ExecuteNonQuery() => _source.Observe(base.ExecuteNonQuery);

        public override object? ExecuteScalar() => _source.Observe(base.ExecuteScalar);

        public override void Prepare() => _source.Observe(base.Prepare);

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            _source.Observe(() => new SourceReader(base.ExecuteDbDataReader(behavior), _source));

        protected override void Dispose(bool disposing) => _source.Observe(() => base.Dispose(disposing));
    }

    private sealed class SourceReader(DbDataReader reader, FaultAwareCopySource source) : DbDataReader
    {
        public override object this[int ordinal] => source.Observe(() => reader[ordinal]);

        public override object this[string name] => source.Observe(() => reader[name]);

        public override int Depth => source.Observe(() => reader.Depth);

        public override int FieldCount => source.Observe(() => reader.FieldCount);

        public override bool HasRows => source.Observe(() => reader.HasRows);

        public override bool IsClosed => source.Observe(() => reader.IsClosed);

        public override int RecordsAffected => source.Observe(() => reader.RecordsAffected);

        public override bool GetBoolean(int ordinal) => source.Observe(() => reader.GetBoolean(ordinal));

        public override byte GetByte(int ordinal) => source.Observe(() => reader.GetByte(ordinal));

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            source.Observe(() => reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length));

        public override char GetChar(int ordinal) => source.Observe(() => reader.GetChar(ordinal));

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            source.Observe(() => reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length));

        public override string GetDataTypeName(int ordinal) => source.Observe(() => reader.GetDataTypeName(ordinal));

        public override DateTime GetDateTime(int ordinal) => source.Observe(() => reader.GetDateTime(ordinal));

        public override decimal GetDecimal(int ordinal) => source.Observe(() => reader.GetDecimal(ordinal));

        public override double GetDouble(int ordinal) => source.Observe(() => reader.GetDouble(ordinal));

        public override Type GetFieldType(int ordinal) => source.Observe(() => reader.GetFieldType(ordinal));

        public override float GetFloat(int ordinal) => source.Observe(() => reader.GetFloat(ordinal));

        public override Guid GetGuid(int ordinal) => source.Observe(() => reader.GetGuid(ordinal));

        public override short GetInt16(int ordinal) => source.Observe(() => reader.GetInt16(ordinal));

        public override int GetInt32(int ordinal) => source.Observe(() => reader.GetInt32(ordinal));

        public override long GetInt64(int ordinal) => source.Observe(() => reader.GetInt64(ordinal));

        public override string GetName(int ordinal) => source.Observe(() => reader.GetName(ordinal));

        public override int GetOrdinal(string name) => source.Observe(() => reader.GetOrdinal(name));

        public override string GetString(int ordinal) => source.Observe(() => reader.GetString(ordinal));

        public override object GetValue(int ordinal) => source.Observe(() => reader.GetValue(ordinal));

        public override int GetValues(object[] values) => source.Observe(() => reader.GetValues(values));

        public override bool IsDBNull(int ordinal) => source.Observe(() => reader.IsDBNull(ordinal));

        public override bool NextResult() => source.Observe(reader.NextResult);

        public override bool Read() => source.Observe(reader.Read);

        public override DataTable? GetSchemaTable() => source.Observe(reader.GetSchemaTable);

        public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

        public void Abort() => source.Observe(((TursoDataReader)reader).Abort);

        public override void Close() => source.Observe(reader.Close);

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    source.Observe(reader.Dispose);
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
