namespace TerraBase.Features.Postgres;

internal static class PgOid
{
    public const int Bool = 16;
    public const int Bytea = 17;
    public const int Int8 = 20;
    public const int Int2 = 21;
    public const int Int4 = 23;
    public const int Text = 25;
    public const int Json = 114;
    public const int Float4 = 700;
    public const int Float8 = 701;
    public const int Unknown = 705;
    public const int Bpchar = 1042;
    public const int Varchar = 1043;
    public const int Date = 1082;
    public const int Timestamp = 1114;
    public const int Timestamptz = 1184;
    public const int Uuid = 2950;
    public const int Jsonb = 3802;
    public const int Numeric = 1700;
}
