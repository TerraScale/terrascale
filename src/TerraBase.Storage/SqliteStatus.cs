using System.Globalization;

namespace TerraBase.Storage;

internal readonly record struct SqliteStatus(int Code, string Message)
{
    private const int UnsupportedStatement = -1;

    public static SqliteStatus Success { get; } = new(SqliteNative.Ok, "ok");

    public bool IsOk => Code is SqliteNative.Ok or SqliteNative.Row or SqliteNative.Done;

    public int Primary => Code & 0xFF;

    public bool IsBusy => Primary is SqliteNative.Busy or SqliteNative.Locked;

    public static SqliteStatus Fail(string message) => new(SqliteNative.Error, message);

    public static SqliteStatus Unsupported(string message) => new(UnsupportedStatement, message);

    public SqlError ToSqlError(int statementIndex) => new(ErrorCode(), Message, SqlState(), statementIndex, Code);

    public StorageBusy ToBusy() => new(Message, TimeSpan.FromMilliseconds(5));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"SQLite {Code}: {Message}");

    private string ErrorCode() => Code switch
    {
        SqliteNative.ConstraintPrimaryKey => "primary_key_violation",
        SqliteNative.ConstraintUnique => "unique_violation",
        SqliteNative.ConstraintForeignKey => "foreign_key_violation",
        SqliteNative.ConstraintNotNull => "not_null_violation",
        SqliteNative.ConstraintCheck => "check_violation",
        _ => Primary switch
        {
            SqliteNative.Constraint => "constraint_violation",
            SqliteNative.Busy or SqliteNative.Locked => "storage_busy",
            SqliteNative.ReadOnly => "read_only",
            SqliteNative.Interrupt => "query_canceled",
            SqliteNative.Auth => "not_authorized",
            SqliteNative.Schema => "serialization_conflict",
            SqliteNative.TooBig => "too_large",
            SqliteNative.Corrupt or SqliteNative.NotADatabase => "storage_corrupted",
            SqliteNative.IoError or SqliteNative.Full => "io_error",
            SqliteNative.Mismatch => "datatype_mismatch",
            SqliteNative.Range => "parameter_out_of_range",
            SqliteNative.Misuse => "misuse",
            _ => "sql_error",
        },
    };

    private string SqlState() => Code switch
    {
        SqliteNative.ConstraintUnique or SqliteNative.ConstraintPrimaryKey => "23505",
        SqliteNative.ConstraintForeignKey => "23503",
        SqliteNative.ConstraintNotNull => "23502",
        SqliteNative.ConstraintCheck => "23514",
        UnsupportedStatement => "0A000",
        _ => Primary switch
        {
            SqliteNative.Constraint => "23000",
            SqliteNative.Busy or SqliteNative.Locked => "55P03",
            SqliteNative.ReadOnly => "25006",
            SqliteNative.Interrupt => "57014",
            SqliteNative.Auth => "42501",
            SqliteNative.Schema => "40001",
            SqliteNative.TooBig => "54000",
            SqliteNative.Corrupt or SqliteNative.NotADatabase => "XX001",
            SqliteNative.IoError or SqliteNative.Full => "58030",
            SqliteNative.Mismatch => "42804",
            SqliteNative.Range => "22023",
            SqliteNative.Error when Message.StartsWith("no such table", StringComparison.Ordinal) => "42P01",
            SqliteNative.Error when Message.StartsWith("no such column", StringComparison.Ordinal) => "42703",
            SqliteNative.Error when Message.Contains("syntax error", StringComparison.Ordinal) => "42601",
            SqliteNative.Error when Message.Contains("already exists", StringComparison.Ordinal) => "42P07",
            _ => "XX000",
        },
    };
}
