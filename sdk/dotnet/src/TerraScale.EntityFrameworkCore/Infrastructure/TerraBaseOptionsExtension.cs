using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.DotNet.TerraBase;
using TerraScale.EntityFrameworkCore.Storage;

namespace TerraScale.EntityFrameworkCore.Infrastructure;

public sealed class TerraBaseOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? info;

    public TerraBaseOptionsExtension()
    {
    }

    private TerraBaseOptionsExtension(TerraBaseOptionsExtension copyFrom)
        : base(copyFrom)
    {
        Database = copyFrom.Database;
        Consistency = copyFrom.Consistency;
    }

    public TerraBaseSqlDatabase? Database { get; private set; }

    public TerraBaseConsistencyMode? Consistency { get; private set; }

    public override DbContextOptionsExtensionInfo Info => info ??= new ExtensionInfo(this);

    public TerraBaseOptionsExtension WithDatabase(TerraBaseSqlDatabase database)
    {
        var clone = (TerraBaseOptionsExtension)Clone();
        clone.Database = database;
        return clone;
    }

    public TerraBaseOptionsExtension WithConsistency(TerraBaseConsistencyMode consistency)
    {
        var clone = (TerraBaseOptionsExtension)Clone();
        clone.Consistency = consistency;
        return clone;
    }

    public override void ApplyServices(IServiceCollection services) => services.AddEntityFrameworkTerraBase();

    public override void Validate(IDbContextOptions options)
    {
        base.Validate(options);
        if ((Database is null) == string.IsNullOrWhiteSpace(ConnectionString) || Connection is not null)
        {
            throw new InvalidOperationException("Configure TerraBase with exactly one of a TerraBaseSqlDatabase or a connection string.");
        }
    }

    protected override RelationalOptionsExtension Clone() => new TerraBaseOptionsExtension(this);

    private sealed class ExtensionInfo(TerraBaseOptionsExtension extension) : RelationalExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => true;

        public override string LogFragment => "using TerraBase " + base.LogFragment
            + (extension.Consistency is { } consistency ? "Consistency=" + TerraBaseConsistencyNames.Format(consistency) + " " : "");

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["TerraBase"] = "1";
            debugInfo["TerraBase:Consistency"] = extension.Consistency is { } consistency ? TerraBaseConsistencyNames.Format(consistency) : "default";
        }
    }
}
