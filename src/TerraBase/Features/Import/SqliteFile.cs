using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text;

namespace TerraBase.Features.Import;

/// <summary>
/// A bounded, read-only reader of the SQLite 3 file format (https://www.sqlite.org/fileformat2.html) for untrusted
/// files: no SQLite or Turso engine opens the file and no SQL in it runs (docs/terrabase/import.md section 1). It walks
/// table b-trees for rows; the schema's SQL text is only tokenized. Every page is visited at most once per walk, trees
/// are at most 32 levels deep, rows at most 16 MiB, and any inconsistency stops reading with <c>malformed_source</c>.
/// </summary>
internal sealed class SqliteFile : IImportSource
{
    private const int MaxDepth = 32;
    private const int MaxPayload = ByteLines.MaxLine;
    private const int MaxColumns = 2000;
    private const int MaxSchemaEntries = 20_000;
    private const long MaxSchemaBytes = 16 * 1024 * 1024;

    // Shadow tables of the virtual table modules SQLite ships, by module; any other table is imported.
    private static readonly Dictionary<string, string[]> ShadowSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fts3"] = ["content", "segments", "segdir", "docsize", "stat"],
        ["fts4"] = ["content", "segments", "segdir", "docsize", "stat"],
        ["fts5"] = ["data", "idx", "content", "docsize", "config"],
        ["rtree"] = ["node", "parent", "rowid"],
        ["rtree_i32"] = ["node", "parent", "rowid"],
        ["geopoly"] = ["node", "parent", "rowid"],
    };

    private readonly FileStream _file;
    private readonly int _pageSize;
    private readonly int _usable;
    private readonly uint _pages;
    private readonly Encoding _text;

    private SqliteFile(FileStream file, int pageSize, int usable, uint pages, Encoding text)
    {
        _file = file;
        _pageSize = pageSize;
        _usable = usable;
        _pages = pages;
        _text = text;
    }

    public SourceSchema Schema { get; } = new();

    /// <summary>Opens a staged file and reads its schema; a file that is not a supported SQLite 3 database is refused.</summary>
    public static SqliteFile Open(string path, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.RandomAccess);
        try
        {
            var header = new byte[100];
            if (file.ReadAtLeast(header, 100, throwOnEndOfStream: false) < 100 || !header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            {
                throw new SourceFormatException("unsupported_source_format", "the file is not an unencrypted SQLite 3 database");
            }

            int pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16));
            pageSize = pageSize == 1 ? 65536 : pageSize;
            if (pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0 || header[21] != 64 || header[22] != 32 || header[23] != 32)
            {
                throw new SourceFormatException("unsupported_source_format", "the file is not an unencrypted SQLite 3 database");
            }

            if (header[18] == 2 || header[19] == 2)
            {
                throw new SourceFormatException("wal_mode_source",
                    "the file is in WAL mode, so rows still in its -wal may be missing; convert it with sqlite3 file.db \"VACUUM INTO 'export.db'\" and upload export.db");
            }

            if (header[18] != 1 || header[19] != 1)
            {
                throw new SourceFormatException("unsupported_source_format", "the file uses a SQLite file format version this reader does not know");
            }

            var usable = pageSize - header[20];
            if (usable < 480)
            {
                throw new SourceFormatException("unsupported_source_format", "the file reserves too much of each page");
            }

            Encoding encoding = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(56)) switch
            {
                0 or 1 => new UTF8Encoding(false, true),
                2 => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
                3 => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
                _ => throw new SourceFormatException("unsupported_source_format", "the file declares an unknown text encoding"),
            };
            if (file.Length % pageSize != 0 || file.Length / pageSize is 0 or > uint.MaxValue)
            {
                throw new SourceFormatException("malformed_source", "the file size is not a whole number of pages");
            }

            var sqlite = new SqliteFile(file, pageSize, usable, (uint)(file.Length / pageSize), encoding);
            sqlite.ReadSchema(cancellationToken);
            return sqlite;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public IEnumerable<SourceRow> Rows(ImportPosition? from, CancellationToken cancellationToken)
    {
        // One page belongs to one table: a page reached twice in a pass, by any table, is malformed.
        var visited = new BitArray(checked((int)_pages + 1));
        for (var index = from?.Table ?? 0; index < Schema.Tables.Count; index++)
        {
            var table = Schema.Tables[index];
            if (table.RootPage == 0)
            {
                continue;
            }

            var stored = table.Columns.Where(column => !column.Generated || column.Stored).ToList();
            var order = RecordOrder(table, stored);
            var skip = from is not null && index == from.Table ? from.Row : 0;
            var row = 0L;
            foreach (var (rowId, record) in Walk(table.RootPage, table.WithoutRowid, 0, visited, cancellationToken))
            {
                if (++row <= skip)
                {
                    continue;
                }

                var values = Decode(record);
                if (values.Count > stored.Count)
                {
                    throw Malformed($"a row of {table.Display} holds more values than the table has columns");
                }

                var ordered = new SourceValue[stored.Count];
                for (var i = 0; i < stored.Count; i++)
                {
                    var position = order[i];
                    // Rows written before ALTER TABLE … ADD COLUMN lack the column: its literal default applies.
                    ordered[i] = position < values.Count ? values[position]
                        : stored[i].ExpressionDefault
                            ? throw Malformed($"a row of {table.Display} lacks column {stored[i].Name}, whose default is an expression")
                            : stored[i].Default;
                    if (stored[i].Name == table.RowidAlias && ordered[i].Kind == SourceKind.Null && rowId is { } id)
                    {
                        ordered[i] = new SourceValue(SourceKind.Integer, Integer: id);
                    }
                }

                yield return new SourceRow(table, ordered, stored, rowId, new ImportPosition(0, null, null, index, row));
            }
        }
    }

    public void Dispose() => _file.Dispose();

    // WITHOUT ROWID records hold the primary key columns first, then the others in declaration order.
    private static int[] RecordOrder(SourceTable table, List<SourceColumn> stored)
    {
        if (!table.WithoutRowid)
        {
            return [.. Enumerable.Range(0, stored.Count)];
        }

        var key = table.PrimaryKey.Select(name => stored.FindIndex(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Where(i => i >= 0).Distinct().ToList();
        var layout = key.Concat(Enumerable.Range(0, stored.Count).Where(i => !key.Contains(i))).ToList();
        var order = new int[stored.Count];
        for (var position = 0; position < layout.Count; position++)
        {
            order[layout[position]] = position;
        }

        return order;
    }

    private void ReadSchema(CancellationToken cancellationToken)
    {
        var entries = new List<(string Type, string Name, long Root, string Sql)>();
        long sqlBytes = 0;
        foreach (var (_, record) in Walk(1, index: false, 0, new BitArray(checked((int)_pages + 1)), cancellationToken))
        {
            var values = Decode(record);
            if (values.Count < 5)
            {
                throw Malformed("the schema table holds a malformed row");
            }

            var sql = values[4].Text ?? "";
            sqlBytes += sql.Length;
            if (entries.Count >= MaxSchemaEntries || sqlBytes > MaxSchemaBytes)
            {
                throw new SourceFormatException("too_many_tables", "the schema holds more than 20000 objects or 16 MiB of SQL");
            }

            entries.Add((values[0].Text ?? "", values[1].Text ?? "", values[3].Kind == SourceKind.Integer ? values[3].Integer : 0, sql));
        }

        var tableNames = entries.Where(entry => entry.Type == "table").Select(entry => entry.Name).ToList();
        if (tableNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tableNames.Count)
        {
            throw Malformed("the schema names one table twice");
        }

        // Virtual tables and the shadow-table names their module creates.
        var virtualTables = entries.Where(entry => entry.Type == "table" && entry.Sql.TrimStart().StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Name).ToList();
        var shadows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(entry => virtualTables.Contains(entry.Name)))
        {
            var tokens = SqlTokens.Read(entry.Sql, sqlite: true);
            var module = tokens.FindIndex(token => token.Is("USING")) is var at and >= 0 && at + 1 < tokens.Count ? tokens[at + 1].Text : "";
            foreach (var suffix in ShadowSuffixes.GetValueOrDefault(module) ?? [])
            {
                shadows.Add(entry.Name + "_" + suffix);
            }
        }

        foreach (var entry in entries)
        {
            var display = ImportNames.Table("main", entry.Name);
            if (entry.Type != "table")
            {
                if (entry.Type is "view" or "trigger" || (entry.Type == "index" && !entry.Name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase)))
                {
                    Schema.Skip(entry.Type, display);
                }
            }
            else if (virtualTables.Contains(entry.Name))
            {
                Schema.Skip("virtual_table", display);
                Schema.Warn($"virtual table {display} is not imported; an index such as FTS or R-tree is rebuilt from its source table, but content held only in the virtual table is lost");
            }
            else if (shadows.Contains(entry.Name))
            {
                Schema.Skip("shadow_table", display);
            }
            else if (entry.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                Schema.Skip("internal_table", display);
            }
            else if (Schema.Tables.Count >= ImportPlanner.MaxTables)
            {
                throw new SourceFormatException("too_many_tables", "an import holds at most 1000 tables");
            }
            else
            {
                Schema.Tables.Add(Table(entry.Name, entry.Root, entry.Sql));
            }
        }
    }

    private SourceTable Table(string name, long root, string sql)
    {
        var tokens = SqlTokens.Read(sql, sqlite: true);
        var open = tokens.FindIndex(token => token.IsSymbol("("));
        var close = open < 0 ? -1 : SqlTokens.Closing(tokens, open);
        var withoutRowid = close > 0 && tokens.Skip(close + 1).Any(token => token.Is("WITHOUT"));
        var valid = close > 0 && root > 0 && root <= _pages;
        var table = new SourceTable { Schema = "main", Name = name, RootPage = valid ? (uint)root : 0, WithoutRowid = withoutRowid };
        if (!valid)
        {
            table.Issues.Add(new("blocking", "malformed_source", null, null, "the table definition cannot be read"));
            return table;
        }

        foreach (var element in SqlTokens.SplitTopLevel(tokens, open + 1, close))
        {
            PostgresDump.Element(table, element, sqlite: true);
        }

        foreach (var check in table.Checks)
        {
            Schema.Skip("check_constraint", table.Display + "." + ImportNames.Quote(check));
        }

        foreach (var column in table.Columns)
        {
            column.Default = Affinity(column.Type, column.Default);
        }

        // A rowid table's single INTEGER PRIMARY KEY column is stored as the rowid (its record value is NULL).
        if (!withoutRowid && table.PrimaryKey is [var key] && table.Column(key, ignoreCase: true) is { Type: "integer" } alias)
        {
            table.RowidAlias = alias.Name;
        }

        foreach (var column in table.Columns.Where(column => column.Generated && !column.Stored))
        {
            table.Issues.Add(new("warning", "not_imported", column.Name, null, "a VIRTUAL generated column is computed, not stored; it is not imported"));
        }

        return table;
    }

    /// <summary>
    /// The cells of a b-tree in key order: (rowid, payload) for table trees, (null, payload) for index trees. Every page is
    /// read at most once per <paramref name="visited"/>, and the cells of a page may not overlap, so a pass reads at most
    /// the file's own bytes.
    /// </summary>
    private IEnumerable<(long? RowId, byte[] Payload)> Walk(uint number, bool index, int depth, BitArray visited, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaxDepth)
        {
            throw Malformed("a b-tree is deeper than 32 levels");
        }

        Visit(number, visited);
        var page = ReadPage(number);
        var at = number == 1 ? 100 : 0;
        var type = page[at];
        var leaf = type is 10 or 13;
        if (type != (index ? (leaf ? 10 : 2) : (leaf ? 13 : 5)))
        {
            throw Malformed("a b-tree page has an unexpected type");
        }

        var cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(at + 3));
        var pointers = at + (leaf ? 8 : 12);
        if (pointers + (cells * 2) > _usable)
        {
            throw Malformed("a b-tree page holds more cells than fit");
        }

        var offsets = new int[cells];
        for (var i = 0; i < cells; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(pointers + (i * 2)));
        }

        CheckCells(page, offsets, pointers + (cells * 2), leaf, index);
        for (var i = 0; i < cells; i++)
        {
            var offset = offsets[i];
            if (!leaf)
            {
                foreach (var entry in Walk(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset)), index, depth + 1, visited, cancellationToken))
                {
                    yield return entry;
                }

                if (!index)
                {
                    continue;
                }

                // An interior cell of an index tree holds an entry of its own, after its left child's entries.
                offset += 4;
            }

            var size = Varint(page, ref offset, _usable);
            long? rowId = index ? null : Varint(page, ref offset, _usable);
            yield return (rowId, Payload(page, offset, size, index, visited));
        }

        if (!leaf)
        {
            foreach (var entry in Walk(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at + 8)), index, depth + 1, visited, cancellationToken))
            {
                yield return entry;
            }
        }
    }

    // Cells lie between the cell pointer array and the usable end, and no two overlap.
    private void CheckCells(byte[] page, int[] offsets, int contentStart, bool leaf, bool index)
    {
        var extents = new (int Start, int End)[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
        {
            var offset = offsets[i];
            if (offset < contentStart || offset >= _usable)
            {
                throw Malformed("a cell lies outside its page");
            }

            var cursor = offset + (leaf ? 0 : 4);
            if (!leaf && !index)
            {
                _ = Varint(page, ref cursor, _usable);
            }
            else
            {
                var size = Varint(page, ref cursor, _usable);
                if (!index)
                {
                    _ = Varint(page, ref cursor, _usable);
                }

                var local = Local(size, index);
                cursor += local + (local < size ? 4 : 0);
            }

            if (cursor > _usable)
            {
                throw Malformed("a cell lies outside its page");
            }

            extents[i] = (offset, cursor);
        }

        Array.Sort(extents);
        for (var i = 1; i < extents.Length; i++)
        {
            if (extents[i].Start < extents[i - 1].End)
            {
                throw Malformed("two cells of a page overlap");
            }
        }
    }

    private void Visit(uint number, BitArray visited)
    {
        if (number == 0 || number > _pages || visited[(int)number])
        {
            throw Malformed("a page is out of range or reached twice");
        }

        visited[(int)number] = true;
    }

    // The bytes of a payload of <paramref name="size"/> stored on its b-tree page (https://www.sqlite.org/fileformat2.html).
    private int Local(long size, bool index)
    {
        if (size is < 0 or > MaxPayload)
        {
            throw Malformed("a row exceeds 16 MiB");
        }

        var u = _usable;
        var x = index ? ((u - 12) * 64 / 255) - 23 : u - 35;
        var m = ((u - 12) * 32 / 255) - 23;
        if (size <= x)
        {
            return (int)size;
        }

        var k = m + (int)((size - m) % (u - 4));
        return k <= x ? k : m;
    }

    private byte[] Payload(byte[] page, int offset, long size, bool index, BitArray visited)
    {
        var u = _usable;
        var local = Local(size, index);

        if (offset + local + (local < size ? 4 : 0) > u)
        {
            throw Malformed("a cell lies outside its page");
        }

        var payload = new byte[size];
        page.AsSpan(offset, local).CopyTo(payload);
        var written = local;
        var next = local < size ? BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset + local)) : 0;
        while (written < size)
        {
            Visit(next, visited);

            var overflow = ReadPage(next);
            var chunk = (int)Math.Min(u - 4, size - written);
            overflow.AsSpan(4, chunk).CopyTo(payload.AsSpan(written));
            written += chunk;
            next = BinaryPrimitives.ReadUInt32BigEndian(overflow);
        }

        return payload;
    }

    /// <summary>The values of a record in column order. Text that is not valid in the file's encoding has a null Text.</summary>
    private List<SourceValue> Decode(byte[] record)
    {
        var offset = 0;
        var headerSize = Varint(record, ref offset, record.Length);
        if (headerSize < offset || headerSize > record.Length)
        {
            throw Malformed("a record header is malformed");
        }

        var types = new List<long>();
        while (offset < headerSize)
        {
            types.Add(Varint(record, ref offset, (int)headerSize));
            if (types.Count > MaxColumns)
            {
                throw Malformed("a record holds more than 2000 values");
            }
        }

        var body = (int)headerSize;
        var values = new List<SourceValue>(types.Count);
        foreach (var type in types)
        {
            var length = type switch
            {
                0 or 8 or 9 => 0L,
                >= 1 and <= 4 => type,
                5 => 6L,
                6 or 7 => 8L,
                >= 12 => (type - (type % 2 == 0 ? 12 : 13)) / 2,
                _ => throw Malformed("a record uses a reserved serial type"),
            };
            if (length < 0 || length > record.Length - body)
            {
                throw Malformed("a record value lies outside its record");
            }

            var bytes = record.AsSpan(body, (int)length);
            values.Add(type switch
            {
                0 => SourceValue.Null,
                8 => new SourceValue(SourceKind.Integer, Integer: 0),
                9 => new SourceValue(SourceKind.Integer, Integer: 1),
                >= 1 and <= 6 => new SourceValue(SourceKind.Integer, Integer: SignedBigEndian(bytes)),
                7 => new SourceValue(SourceKind.Real, Real: BinaryPrimitives.ReadDoubleBigEndian(bytes)),
                _ when type % 2 == 0 => new SourceValue(SourceKind.Blob, Blob: bytes.ToArray()),
                _ => new SourceValue(SourceKind.Text, Text: DecodeText(bytes)),
            });
            body += (int)length;
        }

        return values;
    }

    private string? DecodeText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return _text.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // SQLite applies a column's affinity to a literal default: '5' in an INTEGER column is the integer 5.
    private static SourceValue Affinity(string type, SourceValue value)
    {
        var upper = type.ToUpperInvariant();
        var numeric = upper.Contains("INT", StringComparison.Ordinal) || upper.Contains("REAL", StringComparison.Ordinal)
            || upper.Contains("FLOA", StringComparison.Ordinal) || upper.Contains("DOUB", StringComparison.Ordinal)
            || !(upper.Length == 0 || upper.Contains("CHAR", StringComparison.Ordinal) || upper.Contains("CLOB", StringComparison.Ordinal)
                || upper.Contains("TEXT", StringComparison.Ordinal) || upper.Contains("BLOB", StringComparison.Ordinal));
        var real = upper.Contains("REAL", StringComparison.Ordinal) || upper.Contains("FLOA", StringComparison.Ordinal) || upper.Contains("DOUB", StringComparison.Ordinal);
        if (!numeric || value.Kind != SourceKind.Text || value.Text is not { } text)
        {
            return value is { Kind: SourceKind.Integer } && real ? new SourceValue(SourceKind.Real, Real: value.Integer) : value;
        }

        return !real && long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            ? new SourceValue(SourceKind.Integer, Integer: integer)
            : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? new SourceValue(SourceKind.Real, Real: number)
                : value;
    }

    private static long SignedBigEndian(ReadOnlySpan<byte> bytes)
    {
        long value = (sbyte)bytes[0];
        for (var i = 1; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }

    private static long Varint(byte[] buffer, ref int offset, int limit)
    {
        long value = 0;
        for (var i = 0; i < 9; i++)
        {
            if (offset >= limit)
            {
                throw Malformed("a varint runs past its bounds");
            }

            var b = buffer[offset++];
            if (i == 8)
            {
                return (value << 8) | b;
            }

            value = (value << 7) | (uint)(b & 0x7f);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        return value;
    }

    private byte[] ReadPage(uint number)
    {
        var page = new byte[_pageSize];
        if (RandomAccess.Read(_file.SafeFileHandle, page, (long)(number - 1) * _pageSize) != _pageSize)
        {
            throw Malformed("a page cannot be read");
        }

        return page;
    }

    private static SourceFormatException Malformed(string message) => new("malformed_source", message);
}
