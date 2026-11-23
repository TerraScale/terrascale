using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Infrastructure;

public sealed class TerraBaseDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<TerraBaseDbContextOptionsBuilder, TerraBaseOptionsExtension>(optionsBuilder)
{
    public TerraBaseDbContextOptionsBuilder Consistency(TerraBaseConsistencyMode consistency) => WithOption(extension => extension.WithConsistency(consistency));
}
