using System.Globalization;
using TerraBase.Features.Rules;
using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoShapeReader : IRulesEvaluation
{
    public object? Evaluate(RulesTableSchema table, CompiledCondition condition, IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var projection = string.Join(", ", table.Columns.Select((column, index) => "$value_" + index.ToString(CultureInfo.InvariantCulture) + " AS " + Quote(column)));
        var query = TableCommand($"SELECT {condition.Sql} FROM (SELECT {projection});");
        for (var index = 0; index < table.Columns.Count; index++)
        {
            query.Parameters.AddWithValue("$value_" + index.ToString(CultureInfo.InvariantCulture), values.GetValueOrDefault(table.Columns[index]) ?? DBNull.Value);
        }

        foreach (var (name, value) in parameters)
        {
            query.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        try
        {
            var result = query.ExecuteScalar();
            return result is DBNull ? null : result;
        }
        finally
        {
            query.Parameters.Clear();
        }
    }
}
