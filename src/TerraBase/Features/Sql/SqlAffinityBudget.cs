using Column = TerraBase.Features.Sql.SqlAffinityColumn;

namespace TerraBase.Features.Sql;

/// <summary>Admission and growth limits for execution-local SQL column metadata.</summary>
internal sealed class SqlAffinityBudget
{
    public const long ReservationBytes = 2 * 1024 * 1024;
    private const int MaxColumns = 2000;
    private long _bytes;

    public void Add(List<Column> target, IEnumerable<Column> columns)
    {
        foreach (var column in columns)
        {
            if (target.Count >= MaxColumns)
            {
                throw Limit();
            }

            Charge(1); // Admission and width checks precede every list growth, including wildcard expansion.
            target.Add(column);
        }
    }

    public void Charge(int entries, int characters = 0)
    {
        _bytes += (64L * entries) + (2L * characters);
        if (_bytes > ReservationBytes / 2)
        {
            throw Limit();
        }
    }

    public static FormatException Limit() => new("SQL column metadata exceeds the preparation budget");
}
