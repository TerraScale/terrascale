using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.EntityFrameworkDesign;

public sealed class DashboardDbContextFactory : IDesignTimeDbContextFactory<DashboardDbContext>
{
    public DashboardDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DashboardDbContext>().UseTerraBase("Region=gru;Project=design;Database=design;ApiKey=design").Options);
}
