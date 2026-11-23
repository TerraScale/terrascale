using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Infrastructure;

/// <summary>
/// The TerraBase options of <c>UseTerraBase(…, terraBase =&gt; …)</c>: EF Core's relational options such as
/// <c>MigrationsAssembly</c>, <c>MigrationsHistoryTable</c>, <c>CommandTimeout</c>, <c>MaxBatchSize</c> (at most 256),
/// <c>UseQuerySplittingBehavior</c> and <c>ExecutionStrategy</c>, plus TerraBase's <see cref="Consistency"/>.
/// </summary>
/// <param name="optionsBuilder">The context options being configured.</param>
public sealed class TerraBaseDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<TerraBaseDbContextOptionsBuilder, TerraBaseOptionsExtension>(optionsBuilder)
{
    /// <summary>
    /// Runs the context's queries, saves and <see cref="System.Data.IsolationLevel.Unspecified"/> transactions with
    /// <paramref name="consistency"/> (<c>TerraScale-Consistency</c>). Without it they use the
    /// <see cref="TerraBaseSqlDatabase"/>'s consistency, or the database's <c>defaultConsistency</c>, which is
    /// <see cref="TerraBaseConsistencyMode.Fast"/> unless changed. A transaction begun with
    /// <see cref="System.Data.IsolationLevel.Serializable"/> is always <see cref="TerraBaseConsistencyMode.Strong"/>.
    /// </summary>
    /// <param name="consistency">The consistency mode.</param>
    /// <returns>The same builder.</returns>
    public TerraBaseDbContextOptionsBuilder Consistency(TerraBaseConsistencyMode consistency) => WithOption(extension => extension.WithConsistency(consistency));
}
