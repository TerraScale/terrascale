namespace TerraBase.Engine;

internal static partial class TenantLog
{
    [LoggerMessage(EventId = 2116, Level = LogLevel.Error, Message = "Quarantined {Count} poison remote commits for database {DatabaseId}; their effects were skipped and the origin watermark advanced to {Bookmark}")]
    public static partial void RecoveredCommitsQuarantined(ILogger logger, Ulid databaseId, int count, CausalBookmark bookmark);
}
