using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed record StorageRefusal(int Status, TimeSpan? RetryAfter, string Message);

public static class DashboardStorageOutcomes
{
    public const string ConflictMessage = "This changed in another tab or request at the same time. Try again.";
    private const string BusyMessage = "TerraScale storage is busy. Try again shortly.";
    private const string UnavailableMessage = "A TerraScale service is temporarily unavailable. Try again shortly.";
    private const string UnknownMessage = "The change may or may not have been saved. Reload the page before trying again.";
    private const string RejectedMessage = "The change conflicts with stored data.";
    private static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(1);

    public static bool IsConflict(TerraBaseSqlException error) =>
        error.Code is TerraBaseSqlErrorCodes.SerializationConflict or TerraBaseSqlErrorCodes.RowConflict;

    public static StorageRefusal Classify(TerraBaseSqlException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            TerraBaseSqlErrorCodes.SerializationConflict or TerraBaseSqlErrorCodes.RowConflict =>
                new(StatusCodes.Status409Conflict, DefaultRetry, ConflictMessage),
            TerraBaseSqlErrorCodes.TenantBusy or "storage_busy" or "system_catalog_busy" or TerraBaseSqlErrorCodes.QueryCanceled =>
                new(StatusCodes.Status503ServiceUnavailable, error.RetryAfter ?? DefaultRetry, BusyMessage),
            "strong_quorum_unreachable" or "in_region_quorum_unconfirmed" or "request_timeout" or TerraBaseSqlErrorCodes.Unavailable =>
                new(StatusCodes.Status503ServiceUnavailable, error.RetryAfter ?? DefaultRetry, UnavailableMessage),
            "write_outcome_unknown" or "commit_uncertain" =>
                new(StatusCodes.Status503ServiceUnavailable, null, UnknownMessage),
            TerraBaseSqlErrorCodes.CheckViolation or TerraBaseSqlErrorCodes.NotNullViolation or TerraBaseSqlErrorCodes.ForeignKeyViolation
                or TerraBaseSqlErrorCodes.ConstraintViolation or TerraBaseSqlErrorCodes.UniqueViolation or TerraBaseSqlErrorCodes.PrimaryKeyViolation =>
                new(StatusCodes.Status409Conflict, null, RejectedMessage),
            _ => new(StatusCodes.Status503ServiceUnavailable, error.RetryAfter, UnavailableMessage),
        };
    }
}
