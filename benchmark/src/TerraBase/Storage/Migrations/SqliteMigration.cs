using System.Security.Cryptography;
using System.Text;

namespace TerraBase.Storage.Migrations;

/// <summary>One ordered SQLite schema migration.</summary>
/// <param name="Version">The strictly increasing schema version.</param>
/// <param name="Name">The stable migration name recorded in the database.</param>
/// <param name="Sql">The SQL batch applied for this migration.</param>
public sealed record SqliteMigration
{
    /// <summary>Creates one migration descriptor.</summary>
    public SqliteMigration(int version, string? name, string? sql)
    {
        Version = version;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    /// <summary>The strictly increasing schema version.</summary>
    public int Version { get; }

    /// <summary>The stable migration name.</summary>
    public string Name { get; }

    /// <summary>The SQL batch applied for this migration.</summary>
    public string Sql { get; }

    /// <summary>The stable checksum of the migration SQL.</summary>
    public string Checksum => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql))).ToLowerInvariant();

    /// <summary>Validates the migration descriptor.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The version is not positive.</exception>
    /// <exception cref="ArgumentException">The name or SQL is empty.</exception>
    public void Validate()
    {
        if (Version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Version), "Migration versions must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Sql);
    }
}
