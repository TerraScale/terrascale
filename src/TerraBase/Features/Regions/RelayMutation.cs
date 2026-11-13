using TerraBase.Features.Sql;
using TerraBase.Features.Sql.Routing;

namespace TerraBase.Features.Regions;

internal sealed class RelayMutation
{
    private bool _dirty;
    private bool _mayMutate;
    internal bool MayMutate => _dirty || _mayMutate;

    internal static bool IsRead(string? sql) => sql is not null && SqlRouteParser.Parse(sql) is
        [{ Kind: "select", Ddl: false, Flags.Unknown: false }];

    internal static bool MayMutateStatement(string? sql) =>
        sql is null || !(SqlScript.CountAtMost(sql) == 1 && SqlScript.Classify(sql) is SqlStatementPlan { IsControl: true })
        && !IsRead(sql);

    internal void Sent(bool mayMutate) => _mayMutate |= mayMutate;

    internal void Answered(bool transaction)
    {
        _dirty = transaction && MayMutate;
        _mayMutate = false;
    }
}
