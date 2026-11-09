using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace TerraScale.EntityFrameworkCore.Update;

internal sealed class TerraBaseUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies) : UpdateAndSelectSqlGenerator(dependencies)
{
    internal const string RowsAffectedGuard = "SELECT iif(changes() = 1, 1, json('TerraBase: the row was changed or deleted'))";

    internal const string RowsAffectedGuardError = "malformed JSON";

    public override ResultSetMapping AppendInsertOperation(
        StringBuilder commandStringBuilder, IReadOnlyModificationCommand command, int commandPosition, out bool requiresTransaction) =>
        AppendInsertReturningOperation(commandStringBuilder, command, commandPosition, out requiresTransaction);

    public override string GenerateNextSequenceValueOperation(string name, string? schema) =>
        throw new NotSupportedException("TerraBase has no sequences; use client-generated ULID keys.");

    protected override ResultSetMapping AppendSelectAffectedCountCommand(StringBuilder commandStringBuilder, string name, string? schema, int commandPosition)
    {
        commandStringBuilder.Append(RowsAffectedGuard).AppendLine(SqlGenerationHelper.StatementTerminator).AppendLine();
        return ResultSetMapping.LastInResultSet | ResultSetMapping.ResultSetWithRowsAffectedOnly;
    }

    protected override void AppendRowsAffectedWhereCondition(StringBuilder commandStringBuilder, int expectedRowsAffected) =>
        commandStringBuilder.Append("changes() = ").Append(expectedRowsAffected);

    protected override void AppendIdentityWhereCondition(StringBuilder commandStringBuilder, IColumnModification columnModification)
    {
        SqlGenerationHelper.DelimitIdentifier(commandStringBuilder, "rowid");
        commandStringBuilder.Append(" = last_insert_rowid()");
    }

    protected override void AppendUpdateColumnValue(
        ISqlGenerationHelper updateSqlGeneratorHelper, IColumnModification columnModification, StringBuilder stringBuilder, string name, string? schema)
    {
        if (columnModification.JsonPath is null or "$")
        {
            base.AppendUpdateColumnValue(updateSqlGeneratorHelper, columnModification, stringBuilder, name, schema);
            return;
        }

        stringBuilder.Append("json_set(");
        updateSqlGeneratorHelper.DelimitIdentifier(stringBuilder, columnModification.ColumnName);
        stringBuilder.Append(", '").Append(columnModification.JsonPath).Append("', ");
        var wrap = columnModification.Property is not { } property || property.GetElementType() is not null || ProviderType(property) == typeof(bool);
        if (wrap) stringBuilder.Append("json(");
        base.AppendUpdateColumnValue(updateSqlGeneratorHelper, columnModification, stringBuilder, name, schema);
        if (wrap) stringBuilder.Append(')');
        stringBuilder.Append(')');
    }

    private static Type ProviderType(IProperty property)
    {
        var type = property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType;
        return Nullable.GetUnderlyingType(type) ?? type;
    }
}
