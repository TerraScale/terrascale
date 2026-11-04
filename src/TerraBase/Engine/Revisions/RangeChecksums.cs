using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TerraBase.Engine.Revisions;

internal sealed record RangeChecksum(string Table, long After, long Last, int Rows, long Bytes, string Hash);

internal sealed record ChecksumCursor(string Table, long After);

internal sealed record RevisionChecksums(DatabaseRevision Revision, IReadOnlyList<RangeChecksum>? Ranges, ChecksumCursor? Next);

internal static class RangeChecksums
{
    public const string Schema = "";
    public const int RangeRows = 256;
    public const long RangeBytes = 4L * 1024 * 1024;
    public const int MaxRanges = 64;
    public const long RoundBytes = 16L * 1024 * 1024;

    private const string HomeObjects = "instr(name, 'turso_sync') = 0 AND instr(tbl_name, 'turso_sync') = 0";

    public const string SchemaSql = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE " + HomeObjects + " ORDER BY type, name";

    public static string TablesSql(string from) =>
        $"SELECT name FROM sqlite_schema WHERE type = 'table' AND name >= '{from.Replace("'", "''", StringComparison.Ordinal)}' AND instr(name, 'turso_cdc') = 0 AND sql NOT LIKE 'CREATE VIRTUAL%' AND {HomeObjects} ORDER BY name";

    public static string RowsSql(string table, long after, long last, int limit) => string.Create(CultureInfo.InvariantCulture,
        $"SELECT rowid, * FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WHERE rowid > {after} AND rowid <= {last} ORDER BY rowid LIMIT {limit}");

    public static string Mismatch(RangeChecksum home, RangeChecksum? replica)
    {
        var where = home.Table.Length == 0 ? "the schema" : string.Create(CultureInfo.InvariantCulture, $"a table's rowids ({home.After}, {home.Last}]");
        return replica is null
            ? where + ", unreadable on the replica"
            : string.Create(CultureInfo.InvariantCulture, $"{where}: {home.Rows} rows and {home.Bytes} bytes on the home, {replica.Rows} and {replica.Bytes} on the replica");
    }

    internal sealed class Hasher : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _scratch = new byte[9];

        public int Rows { get; private set; }

        public long Bytes { get; private set; }

        public long LastRowid { get; private set; }

        public void Add(IReadOnlyList<object?> row)
        {
            _scratch[0] = 0xFF;
            BinaryPrimitives.WriteInt32LittleEndian(_scratch.AsSpan(1), row.Count);
            _hash.AppendData(_scratch, 0, 5);
            for (var index = 0; index < row.Count; index++)
            {
                Append(row[index]);
            }

            if (row.Count > 0 && row[0] is long rowid)
            {
                LastRowid = rowid;
            }

            Rows++;
        }

        public RangeChecksum Finish(string table, long after, long last) =>
            new(table, after, last, Rows, Bytes, Convert.ToHexStringLower(_hash.GetHashAndReset().AsSpan(0, 16)));

        public void Dispose() => _hash.Dispose();

        private void Append(object? value)
        {
            switch (value)
            {
                case null or DBNull:
                    _scratch[0] = 0;
                    _hash.AppendData(_scratch, 0, 1);
                    break;
                case long integer:
                    Fixed(1, integer);
                    break;
                case double real:
                    Fixed(2, BitConverter.DoubleToInt64Bits(real));
                    break;
                case string text:
                    Variable(3, Encoding.UTF8.GetBytes(text));
                    break;
                case byte[] blob:
                    Variable(4, blob);
                    break;
                default:
                    Fixed(1, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        private void Fixed(byte tag, long value)
        {
            _scratch[0] = tag;
            BinaryPrimitives.WriteInt64LittleEndian(_scratch.AsSpan(1), value);
            _hash.AppendData(_scratch, 0, 9);
            Bytes += 8;
        }

        private void Variable(byte tag, byte[] value)
        {
            _scratch[0] = tag;
            BinaryPrimitives.WriteInt32LittleEndian(_scratch.AsSpan(1), value.Length);
            _hash.AppendData(_scratch, 0, 5);
            _hash.AppendData(value);
            Bytes += value.Length;
        }
    }
}
