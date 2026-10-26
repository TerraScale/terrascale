using System.Globalization;
using System.Net.Http.Headers;

namespace TerraScale.DotNet.TerraBase;

// Import contract v1 revision 3 (TerraScale/terrascale docs/terrabase/import.md on feat/postgres-turso-import-engine at 567bfd33): a one-time bulk import of
// an uploaded PostgreSQL or Turso/SQLite export into a new, empty database. Polling JSON only; every request is sent once.
public sealed partial class TerraBaseDatabase
{
    /// <summary>Largest import source part TerraBase accepts in one upload request: 4 MiB.</summary>
    public const int MaxImportSourcePartBytes = 4 * 1024 * 1024;

    private const long MaxImportSourceBytes = 1L << 30;

    /// <summary>
    /// Attaches an import to this new, empty database (<c>POST …/import</c>) in <see cref="TerraBaseImportState.AwaitingSource"/>.
    /// Create the database for this import first (<see cref="TerraBaseProjectClient.CreateDatabaseAsync"/> with the same
    /// project access token), under a fresh name such as <c>import-&lt;lowercase ULID&gt;</c>; send every import call to its
    /// home region. The pinned import contract requires no replicas, which conflicts with minimum-two-region placement;
    /// this is a producer compatibility gap, not a supported single-region creation workflow.
    /// Replaying the identical request returns the job; any other request throws
    /// <see cref="TerraBaseConflictException"/> (<c>import_exists</c>), a database with collections
    /// <c>destination_not_empty</c>, one with replica regions <c>destination_replicated</c>. Requires a ts-auth project
    /// access token of this project with <c>databases:write</c> and <c>documents:write</c>: any other credential (a
    /// TerraBase API key included) gets <see cref="TerraBaseAuthenticationException"/>, a token of another project or
    /// database <see cref="TerraBaseNotFoundException"/>.
    /// </summary>
    /// <param name="format">Kind of export file.</param>
    /// <param name="sourceBytes">Exact size of the file, 1 byte to 1 GiB.</param>
    /// <param name="sourceSha256">Lowercase hex SHA-256 of the file. The source of an import never changes.</param>
    /// <param name="options">Mapping options; null takes TerraBase's defaults.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<TerraBaseImportJob> CreateImportAsync(
        TerraBaseImportFormat format, long sourceBytes, string sourceSha256, TerraBaseImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (sourceBytes is < 1 or > MaxImportSourceBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceBytes), "An import source is 1 byte to 1 GiB.");
        }

        if (sourceSha256 is not { Length: 64 } || !sourceSha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("sourceSha256 must be the lowercase hex SHA-256 of the export file.", nameof(sourceSha256));
        }

        var body = TerraBaseConnection.Json(
            new RequestBody { Format = format, SourceBytes = sourceBytes, SourceSha256 = sourceSha256, Options = options }, Json.RequestBody);
        return (await connection.SendAsync(HttpMethod.Post, path + "/import", body, Json.TerraBaseImportJob, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Reads this database's import (<c>GET …/import</c>); poll it (every 2 seconds is enough) for upload progress, the
    /// preview, copy progress and the result. A database without an import throws <see cref="TerraBaseNotFoundException"/>
    /// (<c>unknown_import</c>). Requires <c>databases:read</c>.
    /// </summary>
    public async Task<TerraBaseImportJob> GetImportAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/import", null, Json.TerraBaseImportJob, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Uploads one part of the source (<c>PUT …/import/source?offset=…</c>, <c>application/octet-stream</c>). Send the file in
    /// order, in parts of at most <see cref="MaxImportSourcePartBytes"/>, each at <paramref name="offset"/> =
    /// <see cref="TerraBaseImportSource.ReceivedBytes"/>. A part that ends at or before it is a replay and returns the job
    /// unchanged, so a part is safe to send again; another offset throws <see cref="TerraBaseConflictException"/>
    /// (<c>offset_mismatch</c>): read the job and continue from its received bytes. The last part moves the job to
    /// <see cref="TerraBaseImportState.Analyzing"/>; a SHA-256 mismatch then returns it to
    /// <see cref="TerraBaseImportState.AwaitingSource"/> (<c>source_checksum_mismatch</c>, nothing received): upload the
    /// same file again. Requires <c>databases:write</c> and <c>documents:write</c>.
    /// </summary>
    /// <param name="offset">Position of <paramref name="part"/> in the source.</param>
    /// <param name="part">1 byte to <see cref="MaxImportSourcePartBytes"/> of the source, sent as is.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<TerraBaseImportJob> UploadImportSourceAsync(long offset, ReadOnlyMemory<byte> part, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (part.Length is < 1 or > MaxImportSourcePartBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(part), "An import source part is 1 byte to 4 MiB.");
        }

        var body = new ReadOnlyMemoryContent(part);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var query = TerraBaseConnection.Query(("offset", offset.ToString(CultureInfo.InvariantCulture)));
        return (await connection.SendAsync(HttpMethod.Put, path + "/import/source" + query, body, Json.TerraBaseImportJob, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Replaces the options and analyzes the staged source again (<c>PATCH …/import</c>), from
    /// <see cref="TerraBaseImportState.Ready"/> or <see cref="TerraBaseImportState.Rejected"/>; the new preview has a new id.
    /// Requires <c>databases:write</c> and <c>documents:write</c>.
    /// </summary>
    public async Task<TerraBaseImportJob> UpdateImportAsync(TerraBaseImportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var body = TerraBaseConnection.Json(new RequestBody { Options = options }, Json.RequestBody);
        return (await connection.SendAsync(HttpMethod.Patch, path + "/import", body, Json.TerraBaseImportJob, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Starts copying exactly the preview the user confirmed (<c>POST …/import/start</c>): it binds the source's SHA-256,
    /// the options and the complete mapping. A stale id throws <see cref="TerraBaseConflictException"/>
    /// (<c>preview_changed</c>), blocking issues <c>preview_blocked</c>. Repeating it with the same id while queued or running
    /// returns the job. Requires <c>databases:write</c> and <c>documents:write</c>.
    /// </summary>
    /// <param name="previewId">The confirmed <see cref="TerraBaseImportPreview.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<TerraBaseImportJob> StartImportAsync(string previewId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(previewId);
        var body = TerraBaseConnection.Json(new RequestBody { PreviewId = previewId }, Json.RequestBody);
        return (await connection.SendAsync(HttpMethod.Post, path + "/import/start", body, Json.TerraBaseImportJob, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Cancels the import (<c>POST …/import/cancel</c>): a running copy stops before its next batch and keeps the documents
    /// already written. Repeating it once cancelled returns the job. Requires <c>databases:write</c>.
    /// </summary>
    public async Task<TerraBaseImportJob> CancelImportAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/import/cancel", null, Json.TerraBaseImportJob, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Resumes a failed or cancelled import (<c>POST …/import/resume</c>): a confirmed copy continues after its last
    /// committed batch under <see cref="TerraBaseImportJob.ConfirmedPreviewId"/>; an unconfirmed one is analyzed again and
    /// needs a new start. While <see cref="TerraBaseImportSource.Staged"/> is false it throws
    /// <see cref="TerraBaseConflictException"/> (<c>source_required</c>): upload the same bytes first. Repeating it while
    /// queued, running or analyzing returns the job. Requires <c>databases:write</c> and <c>documents:write</c>.
    /// </summary>
    public async Task<TerraBaseImportJob> ResumeImportAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Post, path + "/import/resume", null, Json.TerraBaseImportJob, null, cancellationToken).ConfigureAwait(false)).Value;
}
