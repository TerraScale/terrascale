using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Primitives;

namespace TerraBase.Features.Import;

/// <summary>What the analysis of a staged source produced.</summary>
/// <param name="Preview">The preview, unless the source could not be read.</param>
/// <param name="Error">Why the import cannot start, when it cannot.</param>
/// <param name="Checksums">Per source table: rows and checksum of the mapped documents.</param>
/// <param name="PlanDigest">Digest of the plan the preview shows.</param>
internal sealed record ImportAnalysis(ImportPreview? Preview, ImportError? Error, IReadOnlyDictionary<string, TableChecksum> Checksums, string? PlanDigest);

/// <summary>Rows and checksum of one table's mapped documents.</summary>
/// <param name="Rows">Source rows.</param>
/// <param name="Checksum">Hex checksum.</param>
internal sealed record TableChecksum(long Rows, string Checksum);

/// <summary>How one source table imports: its collection, id policy, column mappings and issues.</summary>
internal sealed class TablePlan
{
    public const int MaxIssues = 50;
    public const int MaxColumns = 1600;

    private readonly Dictionary<IReadOnlyList<SourceColumn>, (int[] Key, bool[] Write)> _layouts = [];

    public required SourceTable Source { get; init; }

    public required string Collection { get; init; }

    public required bool Excluded { get; init; }

    public required bool IgnoreCase { get; init; }

    public string IdPolicy { get; set; } = "primary_key";

    public Dictionary<SourceColumn, ColumnMapping> Mappings { get; } = [];

    public HashSet<SourceColumn> ExcludedColumns { get; } = [];

    public List<ImportIssue> Issues { get; } = [];

    public int IssueTotal { get; private set; }

    public long Rows { get; set; }

    public ulong[] Sum { get; } = new ulong[4];

    /// <summary>SQLite: storage classes seen per column (bits: integer, float, string, base64).</summary>
    public Dictionary<SourceColumn, int> Classes { get; } = [];

    public bool Blocked => Issues.Any(issue => issue.Severity == "blocking");

    public void Issue(string severity, string code, string? column, long? row, string message)
    {
        IssueTotal++;
        if (Issues.Count < MaxIssues)
        {
            Issues.Add(new(severity, code, column, row, message));
        }
    }

    /// <summary>The document id and body of row number <paramref name="ordinal"/>, or throws <see cref="ImportValueException"/>.</summary>
    public (string Id, byte[] Json) Map(SourceRow row, long ordinal, string jobId, DateTimeOffset createdAt, ArrayBufferWriter<byte> buffer)
    {
        var (key, write) = Layout(row.Columns);
        buffer.ResetWrittenCount();
        using (var writer = ImportMapping.Writer(buffer))
        {
            writer.WriteStartObject();
            for (var i = 0; i < row.Columns.Count; i++)
            {
                if (!write[i])
                {
                    continue;
                }

                var column = row.Columns[i];
                writer.WritePropertyName(column.Name);
                ImportMapping.Write(writer, row.Values[i], Mappings[column], column.Name);
                if (Classes.TryGetValue(column, out var seen))
                {
                    Classes[column] = seen | row.Values[i].Kind switch
                    {
                        SourceKind.Integer => 1,
                        SourceKind.Real => 2,
                        SourceKind.Text => 4,
                        SourceKind.Blob => 8,
                        _ => 0,
                    };
                }
            }

            writer.WriteEndObject();
        }

        var id = IdPolicy switch
        {
            "rowid" => row.RowId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? throw new ImportValueException("invalid_document_id", "the row has no rowid", null),
            "generated" => ImportMapping.GeneratedId(jobId, createdAt, Source.Display, ordinal),
            _ when key.Length == 1 => ImportMapping.KeyText(row.Values[key[0]], Mappings[row.Columns[key[0]]], row.Columns[key[0]].Name),
            _ => string.Join(':', key.Select(i => ImportNames.Component(ImportMapping.KeyText(row.Values[i], Mappings[row.Columns[i]], row.Columns[i].Name)))),
        };
        if (!ImportNames.ValidId(id))
        {
            throw new ImportValueException("invalid_document_id", "the document id must be 1 to 256 characters without control characters", null);
        }

        var json = buffer.WrittenSpan.ToArray();
        ImportMapping.Check(json);
        return (id, json);
    }

    private (int[] Key, bool[] Write) Layout(IReadOnlyList<SourceColumn> columns)
    {
        if (_layouts.TryGetValue(columns, out var layout))
        {
            return layout;
        }

        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var key = IdPolicy == "primary_key"
            ? Source.PrimaryKey.Select(name => Enumerable.Range(0, columns.Count).FirstOrDefault(i => string.Equals(columns[i].Name, name, comparison), -1)).ToArray()
            : [];
        if (key.Contains(-1))
        {
            throw new ImportValueException("unsupported_primary_key", "the data does not hold every primary key column", null);
        }

        layout = (key, [.. columns.Select(column => !ExcludedColumns.Contains(column) && !column.Generated)]);
        _layouts[columns] = layout;
        return layout;
    }
}

/// <summary>Plans tables from a source and the options, and analyzes every row for the preview.</summary>
internal static class ImportPlanner
{
    public const int MaxTables = 1000;
    public const int MaxPreviewBytes = 512 * 1024;

    /// <summary>The table plans of <paramref name="source"/> under <paramref name="options"/>, in source order.</summary>
    public static List<TablePlan> Plan(IImportSource source, ImportOptionsBody options, bool sqlite)
    {
        var excludes = (options.Exclude ?? []).Select(ImportNames.Parse).OfType<List<string>>().ToList();
        var comparison = sqlite ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Excludes(SourceTable table, string? column)
        {
            return excludes.Any(parts =>
                parts.Count == (column is null ? 2 : 3) && string.Equals(parts[0], table.Schema, comparison) && string.Equals(parts[1], table.Name, comparison)
                && (column is null || string.Equals(parts[2], column, comparison)));
        }

        var plans = new List<TablePlan>();
        foreach (var table in source.Schema.Tables)
        {
            if (table.Partitioned && table.Rows == 0)
            {
                source.Schema.Skip("partitioned_table", table.Display);
                continue;
            }

            var plan = new TablePlan
            {
                Source = table,
                Collection = ImportNames.CollectionFor(table.Schema, table.Name),
                Excluded = Excludes(table, null),
                IgnoreCase = sqlite,
            };
            plans.Add(plan);
            foreach (var issue in table.Issues)
            {
                plan.Issue(issue.Severity, issue.Code, issue.Column, issue.Row, issue.Message);
            }

            if (table.Columns.Count > TablePlan.MaxColumns)
            {
                plan.Issue("blocking", "too_many_columns", null, null, "a table has at most 1600 columns");
            }

            foreach (var column in table.Columns)
            {
                var mapping = sqlite ? ColumnMapping.Storage : ImportMapping.Postgres(column.Type, source is PostgresDump dump ? dump.IsEnum : _ => false);
                plan.Mappings[column] = mapping;
                if (sqlite)
                {
                    plan.Classes[column] = 0;
                }

                if (Excludes(table, column.Name) || column.Generated)
                {
                    plan.ExcludedColumns.Add(column);
                    if (column.Generated && !sqlite)
                    {
                        plan.Issue("warning", "not_imported", column.Name, null, "a generated column is derived from other columns; it is not imported");
                    }
                }
                else if (mapping == ColumnMapping.Unsupported)
                {
                    plan.Issue("blocking", "unsupported_type", column.Name, null, $"{column.Type} has no mapping; exclude the column");
                }
            }

            if (table.PrimaryKey.Count > 0)
            {
                var keyColumns = table.PrimaryKey.Select(name => table.Column(name, sqlite)).ToList();
                if (keyColumns.Any(column => column is null || !ImportMapping.KeyAllowed(plan.Mappings[column])))
                {
                    plan.Issue("blocking", "unsupported_primary_key", null, null,
                        "the primary key uses a column that cannot form a document id (float, json, bytea or unsupported); exclude the table");
                }
            }
            else if (sqlite && !table.WithoutRowid)
            {
                plan.IdPolicy = "rowid";
            }
            else if (!sqlite && options.MissingPrimaryKey == "generate")
            {
                plan.IdPolicy = "generated";
            }
            else
            {
                plan.IdPolicy = "none";
                plan.Issue("blocking", "missing_primary_key", null, null,
                    "the table has no primary key; add one, exclude the table, or set missingPrimaryKey to generate");
            }
        }

        var included = plans.Where(plan => !plan.Excluded).ToList();
        foreach (var plan in included)
        {
            if (ImportNames.Reserved(plan.Collection) || included.Count(other => other.Collection == plan.Collection) > 1)
            {
                plan.Issue("blocking", "collection_name_collision", null, null,
                    $"collection {plan.Collection} is reserved or taken by another table; exclude one of them");
            }
        }

        return plans;
    }

    /// <summary>Reads every row of a staged source and returns its preview, or why it cannot be imported.</summary>
    public static ImportAnalysis Analyze(string path, string format, ImportOptionsBody options, string jobId, DateTimeOffset createdAt, TimeProvider time,
        CancellationToken cancellationToken)
    {
        IImportSource source;
        try
        {
            source = Open(path, format, cancellationToken);
        }
        catch (SourceFormatException error)
        {
            return new(null, new ImportError(error.Code, error.Message, Retryable: false), new Dictionary<string, TableChecksum>(), null);
        }

        using (source)
        {
            var sqlite = format == ImportFormats.Sqlite;
            if (source.Schema.Tables.Count > MaxTables)
            {
                return new(null, new ImportError("too_many_tables", "an import holds at most 1000 tables", Retryable: false), new Dictionary<string, TableChecksum>(), null);
            }

            var plans = Plan(source, options, sqlite);
            var byTable = plans.ToDictionary(plan => plan.Source);
            var buffer = new ArrayBufferWriter<byte>(4096);
            ImportError? fatal = null;
            try
            {
                foreach (var row in source.Rows(null, cancellationToken))
                {
                    if (!byTable.TryGetValue(row.Table, out var plan))
                    {
                        continue;
                    }

                    var ordinal = ++plan.Rows;
                    // Excluded tables, tables blocked as declared and tables with 50 issues are only counted.
                    if (plan.Excluded || plan.IssueTotal >= TablePlan.MaxIssues || plan.Issues.Any(issue => issue.Severity == "blocking" && issue.Row is null))
                    {
                        continue;
                    }

                    try
                    {
                        var (id, json) = plan.Map(row, ordinal, jobId, createdAt, buffer);
                        ImportMapping.Add(plan.Sum, id, EntityPayload.WithoutId(json, id).Span);
                    }
                    catch (ImportValueException invalid)
                    {
                        plan.Issue("blocking", invalid.Code, invalid.Column, ordinal, invalid.Message);
                    }
                }
            }
            catch (SourceFormatException unreadable)
            {
                fatal = new ImportError(unreadable.Code, unreadable.Message, Retryable: false);
            }

            var tables = plans.Select(plan => Body(plan, sqlite)).ToList();
            var blocking = tables.Sum(table => table.Excluded ? 0 : table.Issues.Count(issue => issue.Severity == "blocking"))
                + source.Schema.Issues.Count + (fatal is null ? 0 : 1);
            var warnings = tables.Sum(table => table.Excluded ? 0 : table.Issues.Count(issue => issue.Severity == "warning")) + source.Schema.Warnings.Count;
            var preview = Fit(new ImportPreview(EntityIds.New(time).ToString(), tables, source.Schema.Skipped,
                source.Schema.SkippedTotal > source.Schema.Skipped.Count, blocking, warnings), out var tooLarge);
            var error = fatal
                ?? (tooLarge ? new ImportError("preview_too_large", "the preview exceeds 512 KiB; exclude tables or import them separately", Retryable: false) : null)
                ?? source.Schema.Issues.Select(issue => new ImportError(issue.Code, issue.Message, Retryable: false)).FirstOrDefault()
                ?? tables.Where(table => !table.Excluded).SelectMany(table => table.Issues.Where(issue => issue.Severity == "blocking")
                    .Select(issue => new ImportError(issue.Code, issue.Message, Retryable: false, table.Source, issue.Row, issue.Column))).FirstOrDefault();
            var checksums = plans.Where(plan => !plan.Excluded).ToDictionary(plan => plan.Source.Display, plan => new TableChecksum(plan.Rows, ImportMapping.Hex(plan.Sum)));
            return new(preview, error, checksums, Digest(plans, sqlite));
        }
    }

    /// <summary>Opens a staged source of <paramref name="format"/> and reads its schema.</summary>
    public static IImportSource Open(string path, string format, CancellationToken cancellationToken) => format == ImportFormats.Sqlite
        ? SqliteFile.Open(path, cancellationToken)
        : PostgresDump.Open(path, cancellationToken);

    /// <summary>A digest of the parts of a preview the copy applies, to check that a new plan still matches a confirmed preview.</summary>
    public static string Digest(IEnumerable<TablePlan> plans, bool sqlite)
    {
        var text = new StringBuilder(sqlite ? "sqlite\n" : "postgres\n");
        foreach (var plan in plans)
        {
            text.Append(plan.Source.Display).Append('\0').Append(plan.Collection).Append('\0').Append(plan.Excluded).Append('\0').Append(plan.IdPolicy).Append('\0')
                .AppendJoin('\u0001', plan.Source.PrimaryKey).Append('\0');
            foreach (var column in plan.Source.Columns)
            {
                text.Append(column.Name).Append('\u0001').Append(plan.ExcludedColumns.Contains(column)).Append('\u0001')
                    .Append(plan.Mappings[column]).Append('\0');
            }

            text.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static ImportTable Body(TablePlan plan, bool sqlite)
    {
        var columns = plan.Source.Columns.Select(column =>
        {
            var mapping = plan.Mappings[column];
            var name = mapping == ColumnMapping.Storage ? StorageName(plan.Classes.GetValueOrDefault(column)) : ImportMapping.Name(mapping);
            return new ImportColumn(column.Name, column.Type, name, !column.NotNull, plan.ExcludedColumns.Contains(column));
        }).ToList();
        if (sqlite)
        {
            foreach (var column in plan.Source.Columns.Where(column => !plan.ExcludedColumns.Contains(column)
                && System.Numerics.BitOperations.PopCount((uint)plan.Classes.GetValueOrDefault(column)) > 1))
            {
                plan.Issue("warning", "mixed_storage_classes", column.Name, null,
                    $"the column holds {StorageName(plan.Classes[column]).Replace("+", ", ", StringComparison.Ordinal)} values; each keeps its own JSON type");
            }
        }

        return new ImportTable(plan.Source.Display, plan.Collection, plan.Excluded, plan.Rows, plan.IdPolicy, plan.Source.PrimaryKey,
            columns, plan.Source.Constraints, plan.Issues);
    }

    private static string StorageName(int classes)
    {
        var names = new List<string>(4);
        if ((classes & 1) != 0)
        {
            names.Add("integer");
        }

        if ((classes & 2) != 0)
        {
            names.Add("float");
        }

        if ((classes & 4) != 0)
        {
            names.Add("string");
        }

        if ((classes & 8) != 0)
        {
            names.Add("base64");
        }

        return names.Count == 0 ? "null" : string.Join('+', names);
    }

    // A preview over 512 KiB drops columns and constraints, then tables, so the job record stays within TerraBase's limits.
    private static ImportPreview Fit(ImportPreview preview, out bool tooLarge)
    {
        tooLarge = Size(preview) > MaxPreviewBytes;
        if (!tooLarge)
        {
            return preview;
        }

        var reduced = preview with
        {
            Tables = [.. preview.Tables.Select(table => table with { Columns = [], Constraints = [], Issues = [.. table.Issues.Take(5)] })],
        };
        return Size(reduced) <= MaxPreviewBytes ? reduced : reduced with { Tables = [] };
    }

    private static int Size(ImportPreview preview) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(preview, Hosting.TerraBaseJsonContext.Default.ImportPreview).Length;
}
