namespace TerraScale.Authentication.Storage;

public abstract class Change
{
    private protected Change(string? version) => Version = version;

    public string? Version { get; }

    internal virtual bool Assertion => false;

    internal abstract string Identity { get; }

    internal abstract string? Expected { get; }

    internal abstract void Write(WriteBatch batch);

    public static Change Insert<T>(T entity)
        where T : class => new Inserted<T>(entity);

    public static Change Update<T>(T entity, string? version = null)
        where T : class => new Updated<T>(entity, version);

    public static Change Delete<T>(T entity, string? version = null)
        where T : class => new Deleted<T>(entity, version);

    public static Change Assert<T>(T entity, string? version = null)
        where T : class => new Asserted<T>(entity, version);

    public static Change Assert<T>(Versioned<T> current)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(current);
        return new Asserted<T>(current.Value, current.Version);
    }

    public static Change Update<T>(Versioned<T> current, T updated)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(current);
        return new Updated<T>(updated, current.Version);
    }

    public static Change Delete<T>(Versioned<T> current)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(current);
        return new Deleted<T>(current.Value, current.Version);
    }

    private static string Required<T>(Table<T> table, T entity, string? version)
        where T : class =>
        table.Versioned
            ? version ?? table.VersionOf(entity) ?? throw new ArgumentException("A conditional write needs the version it read.", nameof(version))
            : throw new InvalidOperationException($"{typeof(T).Name} has no version.");

    private sealed class Inserted<T>(T entity) : Change(Table<T>.Of.Versioned ? AuthIds.New() : null)
        where T : class
    {
        internal override string Identity => Table<T>.Of.Identity(entity);

        internal override string? Expected => Version;

        internal override void Write(WriteBatch batch) => Table<T>.Of.Insert(batch, entity, Version);
    }

    private sealed class Updated<T>(T entity, string? version) : Change(AuthIds.New())
        where T : class
    {
        private readonly string expected = Required(Table<T>.Of, entity, version);

        internal override string Identity => Table<T>.Of.Identity(entity);

        internal override string? Expected => expected;

        internal override void Write(WriteBatch batch) => Table<T>.Of.Update(batch, entity, expected, Version ?? throw new InvalidOperationException("An update writes a new version."));
    }

    private sealed class Deleted<T>(T entity, string? version) : Change(null)
        where T : class
    {
        private readonly string? expected = Table<T>.Of.Versioned ? Required(Table<T>.Of, entity, version) : null;

        internal override string Identity => Table<T>.Of.Identity(entity);

        internal override string? Expected => expected;

        internal override void Write(WriteBatch batch) => Table<T>.Of.Delete(batch, entity, expected);
    }

    private sealed class Asserted<T>(T entity, string? version) : Change(null)
        where T : class
    {
        private readonly string expected = Required(Table<T>.Of, entity, version);

        internal override bool Assertion => true;

        internal override string Identity => Table<T>.Of.Identity(entity);

        internal override string? Expected => expected;

        internal override void Write(WriteBatch batch) => Table<T>.Of.Assert(batch, entity, expected);
    }
}
