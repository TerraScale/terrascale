using MessagePack;

namespace TerraScale.TerraBase.Contracts
{
    /// <summary>
    /// A TerraBase refusal, carried in the <see cref="TerraBaseRpc.FaultTrailer"/> trailer of the failed call. It keeps the
    /// public API's HTTP status and error code, so callers branch on the same values as on the REST API.
    /// </summary>
    /// <param name="Status">The HTTP status TerraBase answered, for example 404, 409, 412 or 421.</param>
    /// <param name="Code">The stable error code, for example <c>not_found</c> or <c>precondition_failed</c>.</param>
    /// <param name="Message">The human-readable message.</param>
    /// <param name="Region">The region that answered a wrong-region refusal.</param>
    /// <param name="HomeRegion">The database's home region of a wrong-region refusal.</param>
    /// <param name="Regions">The database's regions of a wrong-region refusal.</param>
    /// <param name="OperationIndex">The failing operation of a transaction.</param>
    /// <param name="RetryAfterSeconds">How long to wait before retrying a 429 or 503.</param>
    /// <param name="Details">The schema validation issues of a 400.</param>
    /// <param name="SqliteCode">The SQLite error code of a refused SQL statement.</param>
    [MessagePackObject(true)]
    public sealed record TerraBaseFault(
        int Status,
        string Code,
        string Message,
        string? Region = null,
        string? HomeRegion = null,
        IReadOnlyList<string>? Regions = null,
        int? OperationIndex = null,
        int? RetryAfterSeconds = null,
        IReadOnlyList<TerraBaseFaultDetail>? Details = null,
        string? SqliteCode = null);

    /// <summary>One schema validation issue: the failing JSON Pointer and JSON Schema keyword.</summary>
    /// <param name="Path">The JSON Pointer of the failing value.</param>
    /// <param name="Keyword">The failing JSON Schema keyword.</param>
    [MessagePackObject(true)]
    public sealed record TerraBaseFaultDetail(string Path, string Keyword);
}
