namespace TerraScale.Authentication.Storage;

public sealed record Versioned<T>(T Value, string Version);

public static class Versioned
{
    public static Versioned<T>? Of<T>(T? row)
        where T : class =>
        row is null ? null : new(row, Table<T>.Of.VersionOf(row) ?? throw new InvalidOperationException($"{typeof(T).Name} was read without its version."));

    public static IReadOnlyList<Versioned<T>> All<T>(IEnumerable<T> rows)
        where T : class =>
        [.. rows.Select(row => new Versioned<T>(row, Table<T>.Of.VersionOf(row) ?? throw new InvalidOperationException($"{typeof(T).Name} was read without its version.")))];
}
