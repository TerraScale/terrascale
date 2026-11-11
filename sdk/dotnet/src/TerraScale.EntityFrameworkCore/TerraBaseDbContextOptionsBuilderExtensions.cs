using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet.TerraBase;
using TerraScale.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures a <see cref="DbContext"/> to use TerraBase.</summary>
public static class TerraBaseDbContextOptionsBuilderExtensions
{
    extension(DbContextOptionsBuilder optionsBuilder)
    {
        /// <summary>
        /// Uses <paramref name="database"/>: every command goes through its <see cref="TerraBaseSqlClient"/>, with the
        /// HttpClient, handlers and credential (API key, access-token provider or a caller's own headers) it was built with.
        /// </summary>
        /// <param name="database">The TerraBase database.</param>
        /// <param name="terraBaseOptionsAction">Configures EF Core's relational options.</param>
        /// <returns>The options builder.</returns>
        public DbContextOptionsBuilder UseTerraBase(TerraBaseSqlDatabase database, Action<TerraBaseDbContextOptionsBuilder>? terraBaseOptionsAction = null)
        {
            ArgumentNullException.ThrowIfNull(database);
            return optionsBuilder.Apply(optionsBuilder.TerraBaseExtension().WithDatabase(database), terraBaseOptionsAction);
        }

        /// <summary>
        /// Uses the database a connection string names: <c>Project</c>, <c>Database</c>, <c>ApiKey</c> and optionally <c>Region</c>.
        /// Without <c>Endpoint</c> and <c>AuthEndpoint</c> it uses <c>https://api.terrascale.tech</c>; an <c>Endpoint</c> other than
        /// that also needs <c>AuthEndpoint</c>.
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        /// <param name="terraBaseOptionsAction">Configures EF Core's relational options.</param>
        /// <returns>The options builder.</returns>
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
        /// <inheritdoc cref="UseTerraBase(DbContextOptionsBuilder, TerraBaseSqlDatabase, Action{TerraBaseDbContextOptionsBuilder}?)"/>
        public DbContextOptionsBuilder<TContext> UseTerraBase(TerraBaseSqlDatabase database, Action<TerraBaseDbContextOptionsBuilder>? terraBaseOptionsAction = null) =>
            (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder).UseTerraBase(database, terraBaseOptionsAction);

        /// <inheritdoc cref="UseTerraBase(DbContextOptionsBuilder, string, Action{TerraBaseDbContextOptionsBuilder}?)"/>
        public DbContextOptionsBuilder<TContext> UseTerraBase(string connectionString, Action<TerraBaseDbContextOptionsBuilder>? terraBaseOptionsAction = null) =>
            (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder).UseTerraBase(connectionString, terraBaseOptionsAction);
    }
}
