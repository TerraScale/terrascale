using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;

namespace TerraScale.DotNet.TerraBase;

public sealed class TerraBaseDbParameter : DbParameter
{
    private DbType? dbType;

    [AllowNull]
    public override string ParameterName
    {
        get;

        set => field = string.IsNullOrEmpty(value) ? "" : value[0] is '@' or ':' or '$' ? value[1..] : value;
    } = "";

    public override object? Value { get; set; }

    public override DbType DbType
    {
        get => dbType ?? Value switch
        {
            null or DBNull => DbType.Object,
            bool => DbType.Boolean,
            byte or sbyte or short or ushort or int or uint or long or ulong or Enum => DbType.Int64,
            float or double => DbType.Double,
            decimal => DbType.Decimal,
            string or char => DbType.String,
            byte[] => DbType.Binary,
            DateTime => DbType.DateTime,
            DateTimeOffset => DbType.DateTimeOffset,
            DateOnly => DbType.Date,
            TimeOnly or TimeSpan => DbType.Time,
            Guid => DbType.Guid,
            TerraBaseSqlValue => DbType.Object,
            _ => throw new NotSupportedException($"'{Value.GetType()}' is not a supported SQL argument type."),
        };
        set => dbType = value;
    }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn
    {
        get;

        set => field = string.IsNullOrEmpty(value) ? string.Empty : value;
    } = "";

    public override bool SourceColumnNullMapping { get; set; }

    public override byte Precision { get; set; }

    public override byte Scale { get; set; }

    public override void ResetDbType() => dbType = null;

    internal TerraBaseSqlValue ToSqlValue() => TerraBaseDbParameters.ToSqlValue(Value);
}
