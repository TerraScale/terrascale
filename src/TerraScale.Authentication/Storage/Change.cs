using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace TerraScale.Authentication.Storage;

public abstract class Change
{
    private protected Change(string? version) => this.Version = version;

    public string? Version { get; }

    internal virtual bool Assertion => false;

    internal abstract bool Apply(AuthDbContext db);

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

    private static PropertyEntry? Token(EntityEntry entry) =>
        entry.Metadata.FindProperty(AuthDbContext.VersionProperty) is { IsConcurrencyToken: true } ? entry.Property(AuthDbContext.VersionProperty) : null;

    private static string Expected(PropertyEntry token, string? version) =>
        version ?? token.CurrentValue as string ?? throw new ArgumentException("A conditional write needs the version it read.", nameof(version));

    private sealed class Inserted<T>(T entity) : Change(AuthIds.New())
        where T : class
    {
        internal override bool Apply(AuthDbContext db)
        {
            var entry = db.Add(entity);
            if (Token(entry) is { } token)
            {
                token.CurrentValue = this.Version;
            }

            return true;
        }
    }

    private sealed class Updated<T>(T entity, string? version) : Change(AuthIds.New())
        where T : class
    {
        internal override bool Apply(AuthDbContext db)
        {
            var entry = db.Attach(entity);
            var token = Token(entry) ?? throw new InvalidOperationException($"{typeof(T).Name} has no version.");
            var expected = Expected(token, version);
            entry.State = EntityState.Modified;
            token.CurrentValue = this.Version;
            token.OriginalValue = expected;
            return true;
        }
    }

    private sealed class Deleted<T>(T entity, string? version) : Change(null)
        where T : class
    {
        internal override bool Apply(AuthDbContext db)
        {
            var entry = db.Attach(entity);
            if (Token(entry) is { } token)
            {
                token.OriginalValue = Expected(token, version);
            }

            entry.State = EntityState.Deleted;
            return true;
        }
    }

    private sealed class Asserted<T>(T entity, string? version) : Change(null)
        where T : class
    {
        internal override bool Assertion => true;

        internal override bool Apply(AuthDbContext db)
        {
            var probe = db.Entry(entity);
            if (probe.State != EntityState.Detached)
            {
                return version is null || probe.Property(AuthDbContext.VersionProperty).OriginalValue as string == version;
            }

            var expected = Expected(Token(probe) ?? throw new InvalidOperationException($"{typeof(T).Name} has no version."), version);
            if (Tracked(db, probe) is { } tracked)
            {
                return tracked.Property(AuthDbContext.VersionProperty).OriginalValue as string == expected;
            }

            var entry = db.Attach(entity);
            var token = entry.Property(AuthDbContext.VersionProperty);
            token.CurrentValue = expected;
            token.OriginalValue = expected;
            token.IsModified = true;
            return true;
        }

        private static EntityEntry<T>? Tracked(AuthDbContext db, EntityEntry<T> probe)
        {
            var key = probe.Metadata.FindPrimaryKey()!.Properties;
            var values = key.Select(property => probe.Property(property.Name).CurrentValue).ToArray();
            return db.ChangeTracker.Entries<T>().FirstOrDefault(candidate =>
                key.Select(property => candidate.Property(property.Name).CurrentValue).SequenceEqual(values));
        }
    }
}
