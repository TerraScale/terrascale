using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TerraScale.DotNet.TerraBase;
using TerraScale.EntityFrameworkCore.Infrastructure;

namespace TerraScale.EntityFrameworkCore.Storage;

internal sealed class TerraBaseRelationalConnection(RelationalConnectionDependencies dependencies, TerraBaseDataSources sources) : RelationalConnection(dependencies)
{
    protected override DbConnection CreateDbConnection() =>
        new TerraBaseDbConnection(Dependencies.ContextOptions.FindExtension<TerraBaseOptionsExtension>()?.Database ?? sources.Database(GetValidatedConnectionString()));
}
