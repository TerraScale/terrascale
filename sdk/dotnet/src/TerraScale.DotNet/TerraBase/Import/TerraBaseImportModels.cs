using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

// Import contract: TerraScale/terrascale docs/terrabase/import.md and import-ndjson.md at 94481f1f2 (#408).

/// <summary>The kind of export file an import reads. TerraBase never connects to the source database.</summary>
[JsonConverter(typeof(TerraBaseStringEnumConverter<TerraBaseImportFormat>))]
public enum TerraBaseImportFormat
{
    /// <summary>
    /// A UTF-8 <c>pg_dump --format=plain</c> SQL file with <c>COPY … FROM stdin</c> data. Archives, compressed files and
    /// <c>pg_dumpall</c> output are rejected; <c>INSERT</c> statements are blocking issues.
    /// </summary>
    [JsonStringEnumMemberName("postgres_plain")]
    PostgresPlain,

    /// <summary>
    /// One consolidated SQLite 3 file in rollback journal mode (Turso/libSQL included), for example from
    /// <c>VACUUM INTO</c>. A WAL-mode file is rejected (<c>wal_mode_source</c>).
    /// </summary>
    [JsonStringEnumMemberName("sqlite")]
    Sqlite,

    /// <summary>
    /// One JSON object per line. Export lines (<c>{collection,id,document}</c>) keep their collection and ULID id; any other
    /// line goes to <see cref="TerraBaseImportOptions.Collection"/> or its own <c>collection</c> member.
    /// </summary>
    [JsonStringEnumMemberName("ndjson")]
    Ndjson,
}

/// <summary>Where an import job is. <see cref="Completed"/> is terminal; <see cref="Failed"/> and <see cref="Cancelled"/> can resume.</summary>
[JsonConverter(typeof(TerraBaseStringEnumConverter<TerraBaseImportState>))]
public enum TerraBaseImportState
{
    /// <summary>
    /// Waiting for the source parts; also after a SHA-256 mismatch discarded them, or an upload idle for a day was dropped:
    /// upload the same file again from offset 0.
    /// </summary>
    [JsonStringEnumMemberName("awaiting_source")]
    AwaitingSource,

    /// <summary>Checking the SHA-256 and mapping every row to build the preview; also after a deploy changed the mapping of a confirmed preview (<c>preview_changed</c>).</summary>
    [JsonStringEnumMemberName("analyzing")]
    Analyzing,

    /// <summary>The preview has no blocking issue; start it with its id.</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>The preview has blocking issues, or the source is unreadable or unsupported: update the options, or start a new import.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>Started; waiting for this region's import runner.</summary>
    [JsonStringEnumMemberName("queued")]
    Queued,

    /// <summary>Writing documents.</summary>
    [JsonStringEnumMemberName("running")]
    Running,

    /// <summary>Comparing row counts and checksums with the destination.</summary>
    [JsonStringEnumMemberName("validating")]
    Validating,

    /// <summary>Every table was written and validated. Terminal.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>Stopped by <see cref="TerraBaseImportJob.Error"/>; its <see cref="TerraBaseImportError.Retryable"/> says whether resuming can succeed.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>Cancelled. Documents already written stay; resume, or delete the destination database to discard them.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

/// <summary>
/// How an import maps the source. Every imported row gets a generated deterministic ULID document id, identical on every
/// resume; source primary keys (and NDJSON <c>id</c>, <c>_id</c> or <c>$oid</c>) stay ordinary fields.
/// </summary>
/// <param name="Exclude">
/// At most 1000 tables (<c>schema.table</c>) and columns (<c>schema.table.column</c>) to leave out; SQLite's schema is
/// <c>main</c>. Identifiers other than <c>[a-z0-9_]</c> are double-quoted as in SQL. <see langword="null"/> excludes nothing.
/// </param>
/// <param name="Collection">NDJSON only: the collection of every non-export line; export lines keep their own.</param>
public sealed record TerraBaseImportOptions(
    IReadOnlyList<string>? Exclude = null,
    string? Collection = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Exclude is not null) TerraBaseJsonContext.RequireNonNullItems(Exclude);
    }

    internal bool SameAs(TerraBaseImportOptions other) =>
        Collection == other.Collection && (Exclude ?? []).SequenceEqual(other.Exclude ?? [], StringComparer.Ordinal);
}

/// <summary>
/// A one-time import of an export file into a new, empty database. Every import call must reach <see cref="Region"/>;
/// another region answers <see cref="TerraBaseWrongRegionException"/>.
/// </summary>
/// <param name="Id">ULID of the job.</param>
/// <param name="Project">Owning project.</param>
/// <param name="Database">Name of the destination database; address the job through it.</param>
/// <param name="Region">The destination's home region, which serves every import call.</param>
/// <param name="State">Where the job is.</param>
/// <param name="CancelRequested">A cancel is pending; the runner stops before its next batch.</param>
/// <param name="Options">Mapping options of the current preview.</param>
/// <param name="Source">The declared and staged export file.</param>
/// <param name="Preview">The dry-run result; <see langword="null"/> until analysis finishes.</param>
/// <param name="ConfirmedPreviewId">The preview start confirmed; resume copies only under it. <see langword="null"/> until started.</param>
/// <param name="Progress">Write progress; <see langword="null"/> before start.</param>
/// <param name="Validation">Per-table counts and checksums; set when the job completed or failed validation.</param>
/// <param name="Error">The failure that ended or blocks the job.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last change.</param>
/// <param name="StartedAt">When the job was first started.</param>
/// <param name="FinishedAt">When the job last stopped running.</param>
public sealed record TerraBaseImportJob(
    string Id,
    string Project,
    string Database,
    string Region,
    TerraBaseImportState State,
    bool CancelRequested,
    TerraBaseImportOptions Options,
    TerraBaseImportSource Source,
    TerraBaseImportPreview? Preview,
    string? ConfirmedPreviewId,
    TerraBaseImportProgress? Progress,
    TerraBaseImportValidation? Validation,
    TerraBaseImportError? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>The export file an import reads: exactly these bytes, bound by <see cref="Sha256"/>. It never changes.</summary>
/// <param name="Format">Kind of export.</param>
/// <param name="Bytes">Exact size of the source.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the source.</param>
/// <param name="ReceivedBytes">Bytes uploaded so far: the offset of the next part.</param>
/// <param name="Staged">TerraBase holds the complete, verified source; when false after a failure or cancel, upload the same bytes before resuming.</param>
/// <param name="RetainedUntil">When TerraBase drops the staged source; set once the job failed or was cancelled.</param>
public sealed record TerraBaseImportSource(TerraBaseImportFormat Format, long Bytes, string Sha256, long ReceivedBytes, bool Staged, DateTimeOffset? RetainedUntil);

/// <summary>The dry run: what the import would write. Nothing is written until it is started with <see cref="Id"/>.</summary>
/// <param name="Id">Pass to <see cref="TerraBaseDatabase.StartImportAsync"/> to confirm exactly this preview.</param>
/// <param name="Tables">One plan per source table, at most 1000.</param>
/// <param name="Skipped">Source objects that are never imported or executed, at most 200; SQLite internal, virtual and shadow tables among them.</param>
/// <param name="SkippedTruncated">Whether <see cref="Skipped"/> was cut at 200 entries.</param>
/// <param name="Blocking">Issues with severity <c>blocking</c>; start is refused while above zero.</param>
/// <param name="Warnings">Issues with severity <c>warning</c>.</param>
public sealed record TerraBaseImportPreview(
    string Id,
    IReadOnlyList<TerraBaseImportTablePlan> Tables,
    IReadOnlyList<TerraBaseImportSkipped> Skipped,
    bool SkippedTruncated,
    int Blocking,
    int Warnings) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(Tables);
        TerraBaseJsonContext.RequireNonNullItems(Skipped);
    }
}

/// <summary>How one source table becomes a collection.</summary>
/// <param name="Source"><c>schema.table</c>; <c>main.table</c> for SQLite.</param>
/// <param name="Collection">Destination collection name.</param>
/// <param name="Excluded">Whether the options leave the table out.</param>
/// <param name="Rows">Rows in the export.</param>
/// <param name="PrimaryKey">Primary key columns, in key order; they stay ordinary fields.</param>
/// <param name="Columns">Every source column.</param>
/// <param name="Constraints">Constraints recorded from the source; TerraBase enforces none of them.</param>
/// <param name="Issues">Problems found in every row, at most 50.</param>
public sealed record TerraBaseImportTablePlan(
    string Source,
    string Collection,
    bool Excluded,
    long Rows,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<TerraBaseImportColumn> Columns,
    IReadOnlyList<TerraBaseImportConstraint> Constraints,
    IReadOnlyList<TerraBaseImportIssue> Issues) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(PrimaryKey);
        TerraBaseJsonContext.RequireNonNullItems(Columns);
        TerraBaseJsonContext.RequireNonNullItems(Constraints);
        TerraBaseJsonContext.RequireNonNullItems(Issues);
    }
}

/// <summary>One source column and its explicit type mapping.</summary>
/// <param name="Name">Column name; also the document member name.</param>
/// <param name="SourceType">Declared source type, for example <c>bigint</c>.</param>
/// <param name="Mapping">
/// JSON mapping, for example <c>integer</c>, <c>decimal_string</c>, <c>timestamptz</c> or <c>base64</c>. For SQLite, the
/// non-NULL storage classes seen joined by <c>+</c> (for example <c>integer+string</c>, a warning), and the string
/// <c>null</c> when every value is NULL.
/// </param>
/// <param name="Nullable">Whether the source allows null.</param>
/// <param name="Excluded">Whether the options leave the column out.</param>
public sealed record TerraBaseImportColumn(string Name, string SourceType, string Mapping, bool Nullable, bool Excluded);

/// <summary>A source constraint, recorded and never enforced.</summary>
/// <param name="Kind">For example <c>foreign_key</c>; primary, unique and foreign keys and NOT NULL are recorded.</param>
/// <param name="Name">Constraint name; <see langword="null"/> when the source names none.</param>
/// <param name="Columns">Constrained columns.</param>
/// <param name="References">Referenced <c>schema.table(columns)</c> of a foreign key; <see langword="null"/> when none.</param>
/// <param name="Enforced">Always false in contract v1.</param>
public sealed record TerraBaseImportConstraint(string Kind, string? Name, IReadOnlyList<string> Columns, string? References, bool Enforced) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Columns);
}

/// <summary>A problem found in a table.</summary>
/// <param name="Severity"><c>blocking</c> (start is refused) or <c>warning</c>.</param>
/// <param name="Code">For example <c>unsupported_type</c> or <c>collection_name_collision</c>.</param>
/// <param name="Column">The column it concerns; <see langword="null"/> when it concerns the table.</param>
/// <param name="Row">The row it concerns; <see langword="null"/> when none.</param>
/// <param name="Message">What to do about it.</param>
public sealed record TerraBaseImportIssue(string Severity, string Code, string? Column, long? Row, string Message);

/// <summary>A source object that is listed and never imported or executed.</summary>
/// <param name="Kind">For example <c>function</c>, <c>partitioned_table</c>, <c>virtual_table</c> or <c>internal_table</c>.</param>
/// <param name="Name">Qualified name.</param>
public sealed record TerraBaseImportSkipped(string Kind, string Name);

/// <summary>Write progress.</summary>
/// <param name="TablesDone">Tables fully written.</param>
/// <param name="TablesTotal">Tables to write.</param>
/// <param name="RowsWritten">Rows written so far.</param>
/// <param name="RowsTotal">Rows to write.</param>
/// <param name="CurrentTable">The table being copied; <see langword="null"/> when not copying.</param>
public sealed record TerraBaseImportProgress(int TablesDone, int TablesTotal, long RowsWritten, long RowsTotal, string? CurrentTable);

/// <summary>Validation of the written documents against the source.</summary>
/// <param name="Passed">Whether every table matched.</param>
/// <param name="Tables">Per-table results.</param>
public sealed record TerraBaseImportValidation(bool Passed, IReadOnlyList<TerraBaseImportTableValidation> Tables) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Tables);
}

/// <summary>One table's row count and order-independent checksum, source against destination.</summary>
/// <param name="Source">Source table.</param>
/// <param name="Collection">Destination collection.</param>
/// <param name="SourceRows">Rows in the export.</param>
/// <param name="Documents">Documents in the collection.</param>
/// <param name="SourceChecksum">Checksum of the mapped source rows, hex.</param>
/// <param name="DocumentChecksum">The same checksum read back from the destination, hex.</param>
/// <param name="Match">Whether counts and checksums agree.</param>
public sealed record TerraBaseImportTableValidation(
    string Source, string Collection, long SourceRows, long Documents, string SourceChecksum, string DocumentChecksum, bool Match);

/// <summary>The failure that ended or blocks an import.</summary>
/// <param name="Code">For example <c>source_checksum_mismatch</c>, <c>destination_modified</c>, <c>write_failed</c> or <c>validation_failed</c>.</param>
/// <param name="Message">TerraBase's description.</param>
/// <param name="Retryable">Whether resuming can succeed without changing the source.</param>
/// <param name="Table">Source table, when it applies.</param>
/// <param name="Row">Row ordinal in the table, when it applies.</param>
/// <param name="Column">Column, when it applies.</param>
public sealed record TerraBaseImportError(string Code, string Message, bool Retryable, string? Table, long? Row, string? Column);
