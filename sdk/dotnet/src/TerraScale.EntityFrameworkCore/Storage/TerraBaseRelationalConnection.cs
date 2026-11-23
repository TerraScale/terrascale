using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TerraScale.DotNet.TerraBase;
using TerraScale.EntityFrameworkCore.Infrastructure;

namespace TerraScale.EntityFrameworkCore.Storage;

internal sealed class TerraBaseRelationalConnection(RelationalConnectionDependencies dependencies, TerraBaseDataSources sources) : RelationalConnection(dependencies)
{
    protected override DbConnection CreateDbConnection()
    {
        var extension = Dependencies.ContextOptions.FindExtension<TerraBaseOptionsExtension>();
        var database = extension?.Database ?? sources.Database(GetValidatedConnectionString());
        return new TerraBaseDbConnection(extension?.Consistency is { } consistency ? database.WithConsistency(consistency) : database);
    }
}
