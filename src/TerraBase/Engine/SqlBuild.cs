using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;

namespace TerraBase.Engine;

internal static class SqlBuild
{
    public static readonly IReadOnlyDictionary<string, SqlValue> NoNamed = FrozenDictionary<string, SqlValue>.Empty;

    public static SqlStatement Statement(string sql) => new(sql, [], NoNamed);

    public static SqlStatement Statement(string sql, params SqlValue[] args) => new(sql, args, NoNamed);

    public static SqlValue Text(string value) => SqlValue.FromText(value);

    public static SqlValue OptionalText(string? value) => value is null ? SqlValue.Null : SqlValue.FromText(value);

    public static SqlValue Integer(long value) => SqlValue.FromIntegral(value);

    public static SqlValue OptionalInteger(long? value) => value is { } integer ? SqlValue.FromIntegral(integer) : SqlValue.Null;

    public static SqlValue Bool(bool value) => SqlValue.FromIntegral(value ? 1 : 0);

    public static SqlValue Time(DateTimeOffset value) => SqlValue.FromIntegral(value.ToUnixTimeMilliseconds());

    public static SqlValue OptionalTime(DateTimeOffset? value) => value is { } time ? Time(time) : SqlValue.Null;

    public static SqlValue Id(Ulid value) => SqlValue.FromText(value.ToString());

    extension(IReadOnlyList<SqlValue> row)
    {
        public string TextAt(int index) => row[index].TryGetText(out var text) ? text : Format(row[index]);

        public string? OptionalTextAt(int index) => row[index].Kind == SqlValueKind.Null ? null : row.TextAt(index);

        public long IntegerAt(int index) => row[index].TryGetIntegral(out var value) ? value : 0;

        public long? OptionalIntegerAt(int index) => row[index].TryGetIntegral(out var value) ? value : null;

        public bool BoolAt(int index) => row.IntegerAt(index) != 0;

        public DateTimeOffset TimeAt(int index) => DateTimeOffset.FromUnixTimeMilliseconds(row.IntegerAt(index));

        public DateTimeOffset? OptionalTimeAt(int index) => row.OptionalIntegerAt(index) is { } value ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;

        public Ulid IdAt(int index) => Ulid.TryParse(row.TextAt(index), out var id) ? id : Ulid.Empty;
    }

    public static string Format(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => "",
        SqlValueKind.Integral when value.TryGetIntegral(out var integer) => integer.ToString(CultureInfo.InvariantCulture),
        SqlValueKind.Real when value.TryGetReal(out var real) => real.ToString("R", CultureInfo.InvariantCulture),
        SqlValueKind.Text when value.TryGetText(out var text) => text,
        SqlValueKind.Blob when value.TryGetBlob(out var blob) => Convert.ToBase64String(blob.Span),
        SqlValueKind.Integral or SqlValueKind.Real or SqlValueKind.Text or SqlValueKind.Blob => "",
        _ => throw new UnreachableException(),
    };

    public static string Quote(string identifier) => string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
}
