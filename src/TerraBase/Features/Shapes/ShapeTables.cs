using System.Buffers;
using TerraBase.Errors;
using TerraBase.Primitives;

namespace TerraBase.Features.Shapes;

internal enum ShapeTableStatus
{
    Missing,
    Unsupported,
    Stale,
    Current,
}

internal sealed record ShapeTableState(ShapeTableStatus Status, string? Sql, string? Generation, long Since = 0);

internal interface IShapeTableStorage
{
    ValueTask<ShapeTableState> ReadShapeTableAsync(string table, CancellationToken cancellationToken);

    ValueTask<ShapeTableState> RenewShapeTableAsync(string table, CancellationToken cancellationToken);

    ValueTask<ShapeFilterResult> ReadShapeFilterAsync(string table, string generation, ShapeQuery query, CancellationToken cancellationToken);
}

internal readonly union ShapeFilterResult(ShapeFilter, Validation, Unavailable);

internal static class ShapeTables
{
    private const int MaxName = 63;
    private static readonly SearchValues<char> NameChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    internal static bool IsValidName(string? table) =>
        table is { Length: > 0 and <= MaxName } && !char.IsAsciiDigit(table[0]) && !table.AsSpan().ContainsAnyExcept(NameChars)
        && !table.StartsWith("_tb_", StringComparison.OrdinalIgnoreCase) && !table.StartsWith("turso_", StringComparison.OrdinalIgnoreCase)
        && !table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHandle(string? handle) =>
        handle is { Length: 19 } && handle.StartsWith("h1-", StringComparison.Ordinal) && !handle.AsSpan(3).ContainsAnyExcept(LowerHex);

    internal static void Validate(ShapeSealDefinition shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!IsHandle(shape.Handle) || shape.ShapeId.Length != 32 || shape.ShapeId.AsSpan().ContainsAnyExcept(LowerHex)
            || !IsValidName(shape.Table) || !EntityIds.IsValid(shape.Generation))
        {
            throw new ArgumentException("a shape needs its handle, shape id, table and generation", nameof(shape));
        }
    }
}
