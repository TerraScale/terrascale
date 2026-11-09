using System.Text.Json;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal sealed record ShredRecord(string DatabaseId, DateTimeOffset ShreddedAt);

internal sealed class DatabaseShreddedException(DateTimeOffset? shreddedAt)
    : IOException("the database was deleted and its backup data keys destroyed")
{
    public DateTimeOffset? ShreddedAt { get; } = shreddedAt;
}

internal static class ShredRecords
{
    private const int MaxBytes = 4096;

    public static async Task<ShredRecord?> ReadAsync(IObjectStore store, string scope, CancellationToken cancellationToken)
    {
        ShredRecord? record = null;
        var found = await store.ReadAsync(BackupLayout.Shredded(scope), async (content, token) =>
        {
            var buffer = new byte[MaxBytes + 1];
            var length = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
            if (length > MaxBytes)
            {
                throw new InvalidDataException("a backup deletion record exceeds its byte limit");
            }

            try
            {
                record = JsonSerializer.Deserialize(buffer.AsSpan(0, length), BackupPurgeJsonContext.Default.ShredRecord);
            }
            catch (JsonException)
            {
                record = null;
            }
        }, cancellationToken).ConfigureAwait(false);
        return found ? record ?? new ShredRecord("", DateTimeOffset.MinValue) : null;
    }

    public static async Task WriteAsync(IObjectStore store, string scope, ShredRecord record, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(record, BackupPurgeJsonContext.Default.ShredRecord));
        await store.PutAsync(BackupLayout.Shredded(scope), content, cancellationToken).ConfigureAwait(false);
    }
}
