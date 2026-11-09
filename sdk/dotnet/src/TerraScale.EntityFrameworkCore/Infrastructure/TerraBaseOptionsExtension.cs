using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Infrastructure;

/// <summary>
/// The TerraBase provider's options in a context's <see cref="IDbContextOptions"/>: the database as a
/// <see cref="TerraBaseSqlDatabase"/> or a connection string, plus EF Core's relational options. Set it with
/// <c>UseTerraBase</c>.
/// </summary>
public sealed class TerraBaseOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? info;

    /// <summary>Creates options without a database.</summary>
    public TerraBaseOptionsExtension()
    {
    }

    private TerraBaseOptionsExtension(TerraBaseOptionsExtension copyFrom)
        : base(copyFrom) => Database = copyFrom.Database;

    /// <summary>The database every context with these options uses, or null when a connection string names it.</summary>
    public TerraBaseSqlDatabase? Database { get; private set; }

    /// <summary>Describes the options to EF Core's logging and service-provider caching.</summary>
    public override DbContextOptionsExtensionInfo Info => info ??= new ExtensionInfo(this);

    /// <summary>Returns a copy of these options that uses <paramref name="database"/>.</summary>
    /// <param name="database">The TerraBase database, with the HttpClient and credential it was built with.</param>
    /// <returns>The new options.</returns>
    public TerraBaseOptionsExtension WithDatabase(TerraBaseSqlDatabase database)
    {
        var clone = (TerraBaseOptionsExtension)Clone();
        clone.Database = database;
        return clone;
    }

    /// <summary>Registers the TerraBase provider's services.</summary>
    /// <param name="services">EF Core's internal service collection.</param>
    public override void ApplyServices(IServiceCollection services) => services.AddEntityFrameworkTerraBase();

    /// <summary>Checks that exactly one of a <see cref="TerraBaseSqlDatabase"/> or a connection string is set.</summary>
    /// <param name="options">The context's options.</param>
    public override void Validate(IDbContextOptions options)
    {
        base.Validate(options);
        if ((Database is null) == string.IsNullOrWhiteSpace(ConnectionString) || Connection is not null)
        {
            throw new InvalidOperationException("Configure TerraBase with exactly one of a TerraBaseSqlDatabase or a connection string.");
        }
    }

    /// <summary>Copies these options.</summary>
    /// <returns>The copy.</returns>
    protected override RelationalOptionsExtension Clone() => new TerraBaseOptionsExtension(this);

    private sealed class ExtensionInfo(TerraBaseOptionsExtension extension) : RelationalExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => true;

        public override string LogFragment => "using TerraBase " + base.LogFragment;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["TerraBase"] = "1";
    }
}
