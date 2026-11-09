using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.EntityFrameworkDesign;

public sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseTerraBase("Region=gru;Project=design;Database=design;ApiKey=design").Options);
}
