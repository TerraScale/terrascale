namespace TerraBase.Features.Import;

/// <summary>
/// The import's fixed part, document <c>job</c> of the destination's <c>terrascale-import</c> collection (format 1): what
/// was asked for and the current preview with its checksums.
/// </summary>
/// <param name="V">Record format; only 1 is read.</param>
/// <param name="Id">Job id.</param>
/// <param name="ProjectId">Project.</param>
/// <param name="DatabaseId">Destination id.</param>
/// <param name="Database">Destination name.</param>
/// <param name="Region">Home region.</param>
/// <param name="Format">Source format.</param>
/// <param name="SourceBytes">Declared size.</param>
/// <param name="SourceSha256">Declared SHA-256.</param>
/// <param name="Options">Normalized options.</param>
/// <param name="RequestDigest">Identity of the creating request, for replays.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Preview">Current preview.</param>
/// <param name="PlanDigest">Digest of the preview's plan.</param>
/// <param name="Checksums">Per source table: rows and checksum of the mapped documents.</param>
internal sealed record ImportJobRecord(
    int V, string Id, string ProjectId, string DatabaseId, string Database, string Region, string Format, long SourceBytes, string SourceSha256,
    ImportOptionsBody Options, string RequestDigest, DateTimeOffset CreatedAt, ImportPreview? Preview, string? PlanDigest,
    IReadOnlyDictionary<string, TableChecksum>? Checksums);

/// <summary>Where the copy continues, committed with each batch.</summary>
/// <param name="Position">Source position after the last written row.</param>
/// <param name="Rows">Rows read per source table (excluded ones too), for generated ids.</param>
/// <param name="Written">Documents written per source table.</param>
internal sealed record ImportCheckpoint(ImportPosition Position, IReadOnlyDictionary<string, long> Rows, IReadOnlyDictionary<string, long> Written);

/// <summary>The import's changing part, document <c>state</c> (format 1). <c>Rev</c> changes with every write.</summary>
/// <param name="V">Record format; only 1 is read.</param>
/// <param name="Rev">Revision, asserted by every write.</param>
/// <param name="State">State.</param>
/// <param name="CancelRequested">Whether a cancel is pending.</param>
/// <param name="ConfirmedPreviewId">The preview confirmed by start.</param>
/// <param name="Progress">Copy progress.</param>
/// <param name="Checkpoint">Copy checkpoint.</param>
/// <param name="Validation">Validation result.</param>
/// <param name="Error">Failure.</param>
/// <param name="UpdatedAt">Last change.</param>
/// <param name="StartedAt">First copy start.</param>
/// <param name="FinishedAt">End.</param>
/// <param name="RetainedUntil">When the staged source of a failed or cancelled job is deleted.</param>
internal sealed record ImportStateRecord(
    int V, string Rev, string State, bool CancelRequested, string? ConfirmedPreviewId, ImportProgress? Progress, ImportCheckpoint? Checkpoint,
    ImportValidation? Validation, ImportError? Error, DateTimeOffset UpdatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset? RetainedUntil);

/// <summary>
/// The stored form of a record: TerraBase limits a document to 4096 JSON values, which a large preview or validation
/// exceeds, so the record travels as one string member. <c>Rev</c> stays a member of its own for the write assertion.
/// </summary>
/// <param name="V">Format; only 1 is read.</param>
/// <param name="Rev">The state's revision; null for the job.</param>
/// <param name="Record">The record's JSON.</param>
internal sealed record ImportStored(int V, string? Rev, string Record);

/// <summary>
/// The node-local pointer next to a staged source (<c>&lt;data&gt;/.import/&lt;database id&gt;/manifest.json</c>), so the runner can
/// find its job after a restart. Identifiers only.
/// </summary>
/// <param name="OrganizationId">Storage namespace.</param>
/// <param name="ProjectId">Project.</param>
/// <param name="DatabaseId">Destination id.</param>
/// <param name="Database">Destination name.</param>
/// <param name="Bytes">Declared size, reserved against the staging budget.</param>
internal sealed record ImportManifest(string OrganizationId, string ProjectId, string DatabaseId, string Database, long Bytes);
