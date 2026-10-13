namespace TerraBase.Features.Import;

/// <summary>Import options (docs/terrabase/import.md section 2).</summary>
/// <param name="MissingPrimaryKey"><c>reject</c> (default) or <c>generate</c>.</param>
/// <param name="Exclude">Tables (<c>schema.table</c>) and columns (<c>schema.table.column</c>) left out.</param>
public sealed record ImportOptionsBody(string? MissingPrimaryKey, IReadOnlyList<string>? Exclude);

/// <summary><c>POST …/import</c> body.</summary>
/// <param name="Format"><c>postgres_plain</c> or <c>sqlite</c>.</param>
/// <param name="SourceBytes">Exact source size.</param>
/// <param name="SourceSha256">Lowercase hex SHA-256 of the source.</param>
/// <param name="Options">Options; defaults when omitted.</param>
public sealed record CreateImportRequest(string? Format, long? SourceBytes, string? SourceSha256, ImportOptionsBody? Options);

/// <summary><c>PATCH …/import</c> body.</summary>
/// <param name="Options">The new options.</param>
public sealed record UpdateImportRequest(ImportOptionsBody? Options);

/// <summary><c>POST …/import/start</c> body.</summary>
/// <param name="PreviewId">The preview the user confirmed.</param>
public sealed record StartImportRequest(string? PreviewId);

/// <summary>The uploaded source.</summary>
/// <param name="Format">Source format.</param>
/// <param name="Bytes">Declared size.</param>
/// <param name="Sha256">Declared SHA-256.</param>
/// <param name="ReceivedBytes">Bytes staged so far.</param>
/// <param name="Staged">Whether the complete source is staged.</param>
/// <param name="RetainedUntil">When a failed or cancelled job's staged source is deleted.</param>
public sealed record ImportSourceBody(string Format, long Bytes, string Sha256, long ReceivedBytes, bool Staged, DateTimeOffset? RetainedUntil);

/// <summary>One column of a table plan.</summary>
/// <param name="Name">Column name as it appears in the source, quoted when needed.</param>
/// <param name="SourceType">Declared source type, lowercase.</param>
/// <param name="Mapping">Mapping (section 5).</param>
/// <param name="Nullable">Whether the source allows NULL.</param>
/// <param name="Excluded">Whether the options exclude it.</param>
public sealed record ImportColumn(string Name, string SourceType, string Mapping, bool Nullable, bool Excluded);

/// <summary>A recorded, never enforced, constraint.</summary>
/// <param name="Kind"><c>primary_key</c>, <c>unique</c> or <c>foreign_key</c>.</param>
/// <param name="Name">Constraint name, when the source names it.</param>
/// <param name="Columns">Constrained columns.</param>
/// <param name="References">Referenced table and columns, for a foreign key.</param>
/// <param name="Enforced">Always <see langword="false"/>.</param>
public sealed record ImportConstraint(string Kind, string? Name, IReadOnlyList<string> Columns, string? References, bool Enforced);

/// <summary>A preview issue.</summary>
/// <param name="Severity"><c>blocking</c> or <c>warning</c>.</param>
/// <param name="Code">Issue code.</param>
/// <param name="Column">Column, when the issue has one.</param>
/// <param name="Row">1-based row of the table, when the issue has one.</param>
/// <param name="Message">What is wrong and how to proceed.</param>
public sealed record ImportIssue(string Severity, string Code, string? Column, long? Row, string Message);

/// <summary>How one source table imports.</summary>
/// <param name="Source">Source table, <c>schema.table</c>.</param>
/// <param name="Collection">Target collection.</param>
/// <param name="Excluded">Whether the options exclude it.</param>
/// <param name="Rows">Rows in the export.</param>
/// <param name="IdPolicy"><c>primary_key</c>, <c>rowid</c>, <c>generated</c> or <c>none</c>.</param>
/// <param name="PrimaryKey">Primary key columns.</param>
/// <param name="Columns">Columns in source order.</param>
/// <param name="Constraints">Recorded constraints.</param>
/// <param name="Issues">Issues, at most 50.</param>
public sealed record ImportTable(
    string Source, string Collection, bool Excluded, long Rows, string IdPolicy, IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ImportColumn> Columns, IReadOnlyList<ImportConstraint> Constraints, IReadOnlyList<ImportIssue> Issues);

/// <summary>A source object that is not imported.</summary>
/// <param name="Kind">Object kind.</param>
/// <param name="Name">Object name.</param>
public sealed record ImportSkipped(string Kind, string Name);

/// <summary>The dry-run result the user confirms.</summary>
/// <param name="Id">Preview id, confirmed by <c>startImport</c>.</param>
/// <param name="Tables">Table plans.</param>
/// <param name="Skipped">Objects not imported, at most 200.</param>
/// <param name="SkippedTruncated">Whether more were skipped.</param>
/// <param name="Blocking">Blocking issues.</param>
/// <param name="Warnings">Warnings.</param>
public sealed record ImportPreview(
    string Id, IReadOnlyList<ImportTable> Tables, IReadOnlyList<ImportSkipped> Skipped, bool SkippedTruncated, int Blocking, int Warnings);

/// <summary>Copy progress.</summary>
/// <param name="TablesDone">Tables fully written.</param>
/// <param name="TablesTotal">Tables to write.</param>
/// <param name="RowsWritten">Documents written.</param>
/// <param name="RowsTotal">Documents to write.</param>
/// <param name="CurrentTable">Table being written.</param>
public sealed record ImportProgress(int TablesDone, int TablesTotal, long RowsWritten, long RowsTotal, string? CurrentTable);

/// <summary>Validation of one table.</summary>
/// <param name="Source">Source table.</param>
/// <param name="Collection">Collection.</param>
/// <param name="SourceRows">Rows in the export.</param>
/// <param name="Documents">Documents read back.</param>
/// <param name="SourceChecksum">Checksum of the mapped documents.</param>
/// <param name="DocumentChecksum">Checksum read back.</param>
/// <param name="Match">Whether both agree.</param>
public sealed record ImportTableValidation(
    string Source, string Collection, long SourceRows, long Documents, string SourceChecksum, string DocumentChecksum, bool Match);

/// <summary>Validation result.</summary>
/// <param name="Passed">Whether every table matched.</param>
/// <param name="Tables">Per-table results.</param>
public sealed record ImportValidation(bool Passed, IReadOnlyList<ImportTableValidation> Tables);

/// <summary>The failure that ended or blocks the job.</summary>
/// <param name="Code">Error code.</param>
/// <param name="Message">What failed.</param>
/// <param name="Retryable">Whether resuming can succeed.</param>
/// <param name="Table">Source table.</param>
/// <param name="Row">1-based row of that table.</param>
/// <param name="Column">Column.</param>
public sealed record ImportError(string Code, string Message, bool Retryable, string? Table = null, long? Row = null, string? Column = null);

/// <summary>An import job (docs/terrabase/import.md section 2).</summary>
/// <param name="Id">Job id.</param>
/// <param name="Project">Project id.</param>
/// <param name="Database">Destination name.</param>
/// <param name="Region">Home region.</param>
/// <param name="State">State.</param>
/// <param name="CancelRequested">Whether a cancel is pending.</param>
/// <param name="Options">Options.</param>
/// <param name="Source">Source.</param>
/// <param name="Preview">Preview, once analyzed.</param>
/// <param name="ConfirmedPreviewId">The preview the user confirmed.</param>
/// <param name="Progress">Copy progress.</param>
/// <param name="Validation">Validation.</param>
/// <param name="Error">Failure.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last change.</param>
/// <param name="StartedAt">First copy start.</param>
/// <param name="FinishedAt">Completion, failure or cancellation.</param>
public sealed record ImportJobBody(
    string Id, string Project, string Database, string Region, string State, bool CancelRequested, ImportOptionsBody Options,
    ImportSourceBody Source, ImportPreview? Preview, string? ConfirmedPreviewId, ImportProgress? Progress, ImportValidation? Validation,
    ImportError? Error, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>Job states.</summary>
internal static class ImportStates
{
    public const string AwaitingSource = "awaiting_source";
    public const string Analyzing = "analyzing";
    public const string Ready = "ready";
    public const string Rejected = "rejected";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Validating = "validating";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>Source formats.</summary>
internal static class ImportFormats
{
    public const string PostgresPlain = "postgres_plain";
    public const string Sqlite = "sqlite";
}
