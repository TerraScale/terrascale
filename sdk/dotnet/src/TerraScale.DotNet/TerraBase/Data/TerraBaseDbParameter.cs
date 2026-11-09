using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;

namespace TerraScale.DotNet.TerraBase;

/// <summary>One ADO.NET parameter; values convert to the SDK's <see cref="TerraBaseSqlValue"/> argument types.</summary>
public sealed class TerraBaseDbParameter : DbParameter
{
    private DbType? dbType;

    /// <summary>The parameter name without its <c>@</c>, <c>:</c> or <c>$</c> prefix, or empty for a positional <c>?</c> parameter.</summary>
    [AllowNull]
    public override string ParameterName
    {
        get;

        set => field = string.IsNullOrEmpty(value) ? "" : value[0] is '@' or ':' or '$' ? value[1..] : value;
    } = "";

    /// <summary>The argument value; null is SQL NULL.</summary>
    public override object? Value { get; set; }

    /// <summary>The stored type, or the type inferred from <see cref="Value"/>.</summary>
    public override DbType DbType
    {
        get => dbType ?? Value switch
        {
            null => DbType.Object,
            bool => DbType.Boolean,
            byte or sbyte or short or ushort or int or uint or long => DbType.Int64,
            float or double or decimal => DbType.Double,
            string => DbType.String,
            byte[] => DbType.Binary,
            DateTime => DbType.DateTime,
            _ => throw new NotSupportedException($"'{Value.GetType()}' is not a supported SQL argument type."),
        };
        set => dbType = value;
    }

    /// <summary>Only <see cref="System.Data.ParameterDirection.Input"/> is supported.</summary>
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    /// <summary>Whether the value may be null.</summary>
    public override bool IsNullable { get; set; }

    /// <summary>The parameter size; unused.</summary>
    public override int Size { get; set; }

    /// <summary>The source column name; unused.</summary>
    [AllowNull]
    public override string SourceColumn
    {
        get;

        set => field = value ?? "";
    } = "";

    /// <summary>Whether the source is null-mappable; unused.</summary>
    public override bool SourceColumnNullMapping { get; set; }

    /// <summary>The parameter precision; unused.</summary>
    public override byte Precision { get; set; }

    /// <summary>The parameter scale; unused.</summary>
    public override byte Scale { get; set; }

    /// <summary>Drops a stored <see cref="DbType"/> so it is inferred again.</summary>
    public override void ResetDbType() => dbType = null;

    internal TerraBaseSqlValue ToSqlValue() => TerraBaseDbParameters.ToSqlValue(Value);
}
