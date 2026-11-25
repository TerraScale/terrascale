using System.Globalization;

namespace TerraBase.Storage;

internal readonly record struct SqliteStatus(int Code, string Message)
{
    private const int UnsupportedStatement = -1;
    private const int NumericOutOfRange = -2;
    private const int CharacterNotInRepertoire = -3;

    public static SqliteStatus Success { get; } = new(SqliteNative.Ok, "ok");

    public static SqliteStatus NonFiniteReal { get; } = new(NumericOutOfRange, "REAL values must be finite; Infinity and NaN are not supported");

    public static SqliteStatus InvalidUtf8 { get; } = new(CharacterNotInRepertoire, "TEXT values must be valid UTF-8; store raw bytes as a BLOB");

    public bool IsOk => Code is SqliteNative.Ok or SqliteNative.Row or SqliteNative.Done;

    public int Primary => Code & 0xFF;

    public bool IsBusy => Primary is SqliteNative.Busy or SqliteNative.Locked or SqliteNative.NoMem;

    public bool IsEnvironmental => Primary is SqliteNative.Internal or SqliteNative.Perm or SqliteNative.Abort or SqliteNative.Busy
        or SqliteNative.Locked or SqliteNative.NoMem or SqliteNative.ReadOnly or SqliteNative.Interrupt or SqliteNative.IoError
        or SqliteNative.Corrupt or SqliteNative.Full or SqliteNative.CantOpen or SqliteNative.Protocol or SqliteNative.Schema
        or SqliteNative.Misuse or SqliteNative.NoLargeFile or SqliteNative.NotADatabase;

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
        NumericOutOfRange => "numeric_value_out_of_range",
        CharacterNotInRepertoire => "character_not_in_repertoire",
        _ => Primary switch
        {
            SqliteNative.Constraint => "constraint_violation",
            SqliteNative.Busy or SqliteNative.Locked or SqliteNative.NoMem => "storage_busy",
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
        NumericOutOfRange => "22003",
        CharacterNotInRepertoire => "22021",
        _ => Primary switch
        {
            SqliteNative.Constraint => "23000",
            SqliteNative.Busy or SqliteNative.Locked => "55P03",
            SqliteNative.NoMem => "53200",
            SqliteNative.ReadOnly => "25006",
            SqliteNative.Interrupt => "57014",
            SqliteNative.Auth => "42501",
            SqliteNative.Schema => "40001",
            SqliteNative.TooBig => "54000",
            SqliteNative.Corrupt or SqliteNative.NotADatabase => "XX001",
            SqliteNative.IoError or SqliteNative.Full => "58030",
            SqliteNative.Mismatch => "42804",
            SqliteNative.Range => "22023",
            SqliteNative.Error when IsTooComplex(Message) => "54001",
            SqliteNative.Error when HasTooManyArguments(Message) => "54023",
            SqliteNative.Error when Message.StartsWith("no such table", StringComparison.Ordinal) => "42P01",
            SqliteNative.Error when Message.StartsWith("no such column", StringComparison.Ordinal) => "42703",
            SqliteNative.Error when Message.Contains("syntax error", StringComparison.Ordinal) => "42601",
            SqliteNative.Error when Message.Contains("already exists", StringComparison.Ordinal) => "42P07",
            _ => "XX000",
        },
    };

    private static bool IsTooComplex(string message) =>
        message.StartsWith("Expression tree is too large", StringComparison.Ordinal)
        || message.StartsWith("VIEWs and/or subqueries nested too deep", StringComparison.Ordinal)
        || message.StartsWith("too many terms in compound SELECT", StringComparison.Ordinal)
        || message.StartsWith("LIKE or GLOB pattern too complex", StringComparison.Ordinal)
        || message.StartsWith("triggers nested too deep", StringComparison.Ordinal);

    private static bool HasTooManyArguments(string message) =>
        message.StartsWith("too many SQL variables", StringComparison.Ordinal)
        || message.StartsWith("variable number must be between", StringComparison.Ordinal)
        || message.StartsWith("too many arguments on function", StringComparison.Ordinal);
}
