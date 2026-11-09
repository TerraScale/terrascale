using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet.TerraBase;
using TerraScale.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

public static class TerraBaseDbContextOptionsBuilderExtensions
{
    extension(DbContextOptionsBuilder optionsBuilder)
    {
        public DbContextOptionsBuilder UseTerraBase(TerraBaseSqlDatabase database, Action<TerraBaseDbContextOptionsBuilder>? terraBaseOptionsAction = null)
        {
            ArgumentNullException.ThrowIfNull(database);
            return optionsBuilder.Apply(optionsBuilder.TerraBaseExtension().WithDatabase(database), terraBaseOptionsAction);
        }

        public DbContextOptionsBuilder UseTerraBase(string connectionString, Action<TerraBaseDbContextOptionsBuilder>? terraBaseOptionsAction = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            return optionsBuilder.Apply((TerraBaseOptionsExtension)optionsBuilder.TerraBaseExtension().WithConnectionString(connectionString), terraBaseOptionsAction);
        }

        private TerraBaseOptionsExtension TerraBaseExtension() => optionsBuilder.Options.FindExtension<TerraBaseOptionsExtension>() ?? new TerraBaseOptionsExtension();

        private DbContextOptionsBuilder Apply(TerraBaseOptionsExtension extension, Action<TerraBaseDbContextOptionsBuilder>? terraBaseOptionsAction)
        {
            ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
            terraBaseOptionsAction?.Invoke(new TerraBaseDbContextOptionsBuilder(optionsBuilder));
            return optionsBuilder;
        }
    }

    extension<TContext>(DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        public DbContextOptionsBuilder<TContext> UseTerraBase(TerraBaseSqlDatabase database, Action<TerraBaseDbContextOptionsBuilder>? terraBaseOptionsAction = null) =>
            (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder).UseTerraBase(database, terraBaseOptionsAction);

        public DbContextOptionsBuilder<TContext> UseTerraBase(string connectionString, Action<TerraBaseDbContextOptionsBuilder>? terraBaseOptionsAction = null) =>
            (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder).UseTerraBase(connectionString, terraBaseOptionsAction);
    }
}
