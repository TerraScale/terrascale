using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TerraScale.EntityFrameworkCore.Infrastructure;

public sealed class TerraBaseDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<TerraBaseDbContextOptionsBuilder, TerraBaseOptionsExtension>(optionsBuilder);
