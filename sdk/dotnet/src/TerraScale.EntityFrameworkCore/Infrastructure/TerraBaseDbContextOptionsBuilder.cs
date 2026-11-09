using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TerraScale.EntityFrameworkCore.Infrastructure;

/// <summary>
/// The TerraBase options of <c>UseTerraBase(…, terraBase =&gt; …)</c>: EF Core's relational options such as
/// <c>MigrationsAssembly</c>, <c>MigrationsHistoryTable</c>, <c>CommandTimeout</c>, <c>MaxBatchSize</c> (at most 256),
/// <c>UseQuerySplittingBehavior</c> and <c>ExecutionStrategy</c>.
/// </summary>
/// <param name="optionsBuilder">The context options being configured.</param>
public sealed class TerraBaseDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<TerraBaseDbContextOptionsBuilder, TerraBaseOptionsExtension>(optionsBuilder);
