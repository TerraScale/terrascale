using System.Buffers;
using TerraBase.Configuration;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal enum ShapeCleanupStage { Live, Fenced, Deleted, Complete }

internal enum ShapeCleanupReason { Retention, Floor, Database }

internal enum ShapeCleanupAdmission { Live, Refused, Saturated }

internal sealed record ShapeCleanupObject(string Key, TenantContext Database, Ulid Incarnation, long FloorOffset, long Epoch, string KeyId, long Deadline);

internal sealed record ShapeCleanupItem(string Key, ShapeCleanupStage Stage, int Attempt)
{
    internal const long Bytes = 256;

    internal string Handle => Key[10..29];
}

internal sealed record ShapeCleanupProgress(string Key, ShapeCleanupStage Stage, int Attempt, long NextAttempt, int Parked = 0, bool Held = false);

internal sealed record ShapeCleanupStatus(long Objects, long Bytes, long Handles, long Pending, long Parked, long OldestDue)
{
    internal bool Saturated => Objects >= ShapeCleanupLimits.Objects || Handles >= ShapeCleanupLimits.Handles
        || Bytes > ShapeCleanupLimits.Bytes - ShapeCleanupLimits.RowBytes;
}

internal static class ShapeCleanupLimits
{
    internal const int Objects = 65536;
    internal const int Handles = 4096;
    internal const long Bytes = 64L * 1024 * 1024;
    internal const long RowBytes = 1024;
    internal const int WindowItems = 4096;
    internal const long WindowBytes = 1024 * 1024;
    internal const int PurgeBatch = 100;
    internal const int Transaction = 256;
    internal const int Attempts = 5;
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan Upload = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan Cycle = TimeSpan.FromHours(1);
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    internal static bool Canonical(string key) => key.Length == 94 && key.StartsWith("v1/shapes/h1-", StringComparison.Ordinal)
        && key[29] == '/' && !key.AsSpan(13, 16).ContainsAnyExcept(LowerHex) && !key.AsSpan(30).ContainsAnyExcept(LowerHex);

    internal static string Prefix(string handle) => ShapeStorageSettings.CdnHost + "/v1/shapes/" + handle + "/";

    internal static long RowCost(ShapeCleanupObject item) => 256 + 2L * (item.Key.Length + item.KeyId.Length + 4 * 26 + 19);
}

internal interface IShapeCleanupStore
{
    ValueTask<ShapeCleanupAdmission> ReserveAsync(ShapeCleanupObject item, long now, CancellationToken cancellationToken);

    ValueTask<bool> PublishAsync(string key, long epoch, string keyId, long now, CancellationToken cancellationToken);

    ValueTask ForgetAsync(string key, TenantContext database, CancellationToken cancellationToken);

    ValueTask<(IReadOnlySet<string> Live, bool Saturated)> LiveAsync(IReadOnlyList<string> keys, long now, CancellationToken cancellationToken);

    ValueTask<bool> ProvesAsync(TenantContext database, string handle, long floor, long epoch, string keyId, long now, CancellationToken cancellationToken);

    ValueTask<int> ActivateObjectAsync(string key, ShapeCleanupReason reason, long now, CancellationToken cancellationToken);

    ValueTask<int> ActivateHandleAsync(TenantContext database, string handle, long floor, ShapeCleanupReason reason, long now, CancellationToken cancellationToken);

    ValueTask<int> ActivateDatabaseAsync(TenantContext database, long now, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ShapeCleanupItem>> LeaseAsync(string lease, long now, CancellationToken cancellationToken);

    ValueTask<int> SaveAsync(string lease, IReadOnlyList<ShapeCleanupProgress> progress, long now, CancellationToken cancellationToken);

    ValueTask<int> CollectAsync(long now, CancellationToken cancellationToken);

    ValueTask RecoverAsync(CancellationToken cancellationToken);

    ValueTask<ShapeCleanupStatus> StatusAsync(long now, CancellationToken cancellationToken);
}

internal interface IShapeCleanup
{
    void Wake();

    ValueTask<TimeSpan> DrainAsync(CancellationToken cancellationToken);
}
