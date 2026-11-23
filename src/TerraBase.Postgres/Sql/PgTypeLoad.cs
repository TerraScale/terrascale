using System.Globalization;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal static class PgTypeLoad
{
    private static readonly PgResult Loaded = Build();

    public static PgResult Types() => Loaded;

    private static PgResult Build()
    {
        PgColumn[] columns =
        [
            new("nspname", PgOid.Text),
            new("oid", PgOid.Oid),
            new("typname", PgOid.Text),
            new("typtype", PgOid.Text),
            new("typnotnull", PgOid.Bool),
            new("elemtypoid", PgOid.Oid),
        ];
        List<IReadOnlyList<SqlValue>> rows = [];
        foreach (var type in PgOid.Types)
        {
            rows.Add([Text("pg_catalog"), SqlValue.FromIntegral(type.Oid), Text(type.Name), Text(type.Oid == PgOid.Unknown ? "p" : "b"), SqlValue.FromIntegral(0), SqlValue.Null]);
        }

        foreach (var type in PgOid.Types.Where(static type => type.ArrayOid != 0))
        {
            rows.Add([Text("pg_catalog"), SqlValue.FromIntegral(type.ArrayOid), Text("_" + type.Name), Text("a"), SqlValue.FromIntegral(0), SqlValue.FromIntegral(type.Oid)]);
        }

        return new PgResult(columns, rows, "SELECT " + rows.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static SqlValue Text(string value) => SqlValue.FromText(value);
}
