using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.Shapes;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.Rules;

internal static class RulesReads
{
    internal static object Filter(ShapeQuery query, string table, IReadOnlyList<string> tableColumns, StoredRules? rules, IRulesSchema schema)
    {
        var viewer = query.Viewer ?? throw new ArgumentException("a rules read needs a viewer", nameof(query));
        if (rules is null || Rule(rules.Rules, table) is not { Read: { } read } rule)
        {
            return Refusal("the rules do not allow reading this table", "rule_denied", table);
        }

        var shape = new RulesTableSchema(ShapeTableStatus.Current, tableColumns);
        if (RulesChecks.Compile(shape, read, RulesOperation.Read) is not CompiledCondition condition || !schema.Runs(shape, condition)
            || Readable(rule.Columns, tableColumns) is not { } readable)
        {
            return Refusal("the table's read rule no longer fits the table; deploy the rules again", "rules_invalid", table);
        }

        IReadOnlyList<string> columns = readable;
        if (query.Columns is { } list)
        {
            List<string> chosen = [];
            foreach (var part in list.Split(','))
            {
                var name = part.Trim();
                if (Column(readable, name) is not { } column)
                {
                    return ShapeFilters.ColumnNotReadable(name);
                }

                if (chosen.Contains(column, StringComparer.Ordinal))
                {
                    return new Validation($"columns names {column} twice", "unknown_column");
                }

                chosen.Add(column);
            }

            columns = chosen;
        }

        var sql = condition.Sql;
        if (query.Where is { } where)
        {
            var request = ShapeFilters.Where(where, new RequestScope(readable, true));
            if (request is not CompiledCondition compiled)
            {
                return request;
            }

            sql = $"{sql} AND ({compiled.Sql})";
        }

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        foreach (var parameter in condition.Parameters)
        {
            parameters[parameter.Name] = Value(parameter, viewer.Auth);
        }

        return new ShapeFilter(sql, columns)
        {
            Parameters = parameters.ToFrozenDictionary(StringComparer.Ordinal),
            RulesVersion = rules.Version,
            Binding = Canonical(parameters.Select(pair => (pair.Key[1..], pair.Value))),
        };
    }

    internal static string Identity(RulesAuth auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        List<(string, object?)> values =
        [
            ("auth.uid", auth.Uid), ("auth.org_id", auth.OrgId), ("auth.org_role", auth.OrgRole), ("auth.is_anonymous", auth.IsAnonymous ? 1L : 0L),
        ];
        values.AddRange(auth.Claims.Select(claim => claim.Value is IReadOnlyList<RulesScalar>
            ? ("auth.claims." + claim.Key + "[]", (object?)List(claim.Value))
            : ("auth.claims." + claim.Key, Scalar(claim.Value))));
        return Canonical(values);
    }

    private static Forbidden Refusal(string message, string code, string table) =>
        new(message, code, new Dictionary<string, string> { ["operation"] = "read", ["table"] = table });

    private static RulesTable? Rule(RulesSet rules, string table) =>
        rules.Tables.TryGetValue(table, out var exact)
            ? exact
            : rules.Tables.FirstOrDefault(pair => string.Equals(pair.Key, table, StringComparison.OrdinalIgnoreCase)).Value;

    private static List<string>? Readable(IReadOnlyList<string>? names, IReadOnlyList<string> tableColumns)
    {
        if (names is null)
        {
            return [.. tableColumns];
        }

        List<string> readable = [];
        foreach (var name in names)
        {
            if (Column(tableColumns, name) is not { } column)
            {
                return null;
            }

            readable.Add(column);
        }

        return readable;
    }

    private static string? Column(IReadOnlyList<string> columns, string name) =>
        columns.FirstOrDefault(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase));

    private static object? Value(ConditionParameter parameter, RulesAuth auth) => parameter.Source switch
    {
        ConditionSource.Uid => auth.Uid,
        ConditionSource.OrgId => auth.OrgId,
        ConditionSource.OrgRole => auth.OrgRole,
        ConditionSource.IsAnonymous => auth.IsAnonymous ? 1L : 0L,
        ConditionSource.Claim => auth.Claims.TryGetValue(parameter.Key!, out var claim) ? Scalar(claim) : null,
        ConditionSource.ClaimList => auth.Claims.TryGetValue(parameter.Key!, out var listed) ? List(listed) : null,
        _ => throw new UnreachableException(),
    };

    private static object? Scalar(RulesClaim claim) => claim switch
    {
        string text => text,
        long integer => integer,
        double real => real,
        IReadOnlyList<RulesScalar> => null,
    };

    private static string List(RulesClaim claim)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            switch (claim)
            {
                case IReadOnlyList<RulesScalar> items:
                    foreach (var item in items)
                    {
                        Write(writer, item switch
                        {
                            string text => text,
                            long integer => integer,
                            double real => (object)real,
                        });
                    }

                    break;
                default:
                    Write(writer, Scalar(claim));
                    break;
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Canonical(IEnumerable<(string Name, object? Value)> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values.OrderBy(pair => pair.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                Write(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double real:
                writer.WriteNumberValue(real);
                break;
            default:
                throw new UnreachableException();
        }
    }
}
