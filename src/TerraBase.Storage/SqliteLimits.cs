using System.Globalization;

namespace TerraBase.Storage;

internal static class SqliteLimits
{
    public const int MaxValueBytes = 2 * SqlLimits.MaxWriteBytes;

    private const long MinHardHeapBytes = 256L * 1024 * 1024;

    private static readonly (int Id, int Value)[] UserLimits =
    [
        (SqliteNative.LimitLength, MaxValueBytes),
        (SqliteNative.LimitSqlLength, 1024 * 1024),
        (SqliteNative.LimitExprDepth, 200),
        (SqliteNative.LimitCompoundSelect, 50),
        (SqliteNative.LimitVdbeOp, 250_000),
        (SqliteNative.LimitFunctionArg, 127),
        (SqliteNative.LimitAttached, 0),
        (SqliteNative.LimitLikePatternLength, 1000),
        (SqliteNative.LimitVariableNumber, 8192),
        (SqliteNative.LimitTriggerDepth, 32),
    ];

    private static int _verified;

    public static void ApplyHeapLimits()
    {
        var hard = Math.Max(MinHardHeapBytes, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);
        var soft = hard / 2;
        _ = SqliteNative.HardHeapLimit64(hard);
        _ = SqliteNative.SoftHeapLimit64(soft);
        if (SqliteNative.HardHeapLimit64(-1) != hard || SqliteNative.SoftHeapLimit64(-1) != soft)
        {
            throw new InvalidOperationException("SQLite did not apply the process heap limits.");
        }
    }

    public static SqliteStatus Verify(nint db)
    {
        if (Volatile.Read(ref _verified) != 0)
        {
            return SqliteStatus.Success;
        }

        foreach (var (id, value) in UserLimits)
        {
            _ = SqliteNative.Limit(db, id, value);
            if (SqliteNative.Limit(db, id, int.MaxValue) != value)
            {
                return new SqliteStatus(SqliteNative.Error, string.Concat("sqlite limit ", id.ToString(CultureInfo.InvariantCulture), " was not applied"));
            }
        }

        Volatile.Write(ref _verified, 1);
        return SqliteStatus.Success;
    }

    public static void ApplyUser(nint db)
    {
        foreach (var (id, value) in UserLimits)
        {
            _ = SqliteNative.Limit(db, id, value);
        }
    }

    public static void ApplyEngine(nint db)
    {
        foreach (var (id, _) in UserLimits)
        {
            _ = SqliteNative.Limit(db, id, int.MaxValue);
        }
    }
}
