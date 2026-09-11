using TerraScale.DotNet;
using TerraScale.EntityFrameworkCore;

namespace TerraScale.EntityFrameworkCore.Tests;

public sealed class TerraScaleEntitySetTests
{
    [Test]
    public async Task OptionsExposeCurrentDatabaseContract()
    {
        var options = new TerraScaleEntityFrameworkCoreOptions { DatabaseName = "database" };
        await Assert.That(options.DatabaseName).IsEqualTo("database");
    }
}
