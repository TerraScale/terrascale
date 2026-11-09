using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TerraScale.EntityFrameworkCore.Tests.EntityFrameworkDesign;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseTerraBase("Region=gru;Project=design;Database=design;ApiKey=design").Options);
}
