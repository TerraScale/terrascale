namespace TerraBase.Features.Sql;

/// <summary>SQLite identifiers fold ASCII A-Z only; every non-ASCII UTF-16 code unit stays exact.</summary>
internal sealed class SqlIdentifierComparer : IEqualityComparer<string>
{
    public static SqlIdentifierComparer Instance { get; } = new();

    private SqlIdentifierComparer()
    {
    }

    public bool Equals(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.Length != y.Length)
        {
            return false;
        }

        for (var i = 0; i < x.Length; i++)
        {
            if (Fold(x[i]) != Fold(y[i]))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(string obj)
    {
        var hash = new HashCode();
        foreach (var character in obj)
        {
            hash.Add(Fold(character));
        }

        return hash.ToHashCode();
    }

    public static string LowerAscii(string value) => string.Create(value.Length, value, static (chars, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            chars[i] = Fold(source[i]);
        }
    });

    private static char Fold(char value) => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
}
