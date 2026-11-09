using Microsoft.EntityFrameworkCore.Storage;
using TerraScale.DotNet.TerraBase;
using TerraScale.EntityFrameworkCore.Migrations;

namespace TerraScale.EntityFrameworkCore.Storage;

internal sealed class TerraBaseExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, DefaultMaxRetryCount, DefaultMaxDelay)
{
    protected override bool ShouldRetryOn(Exception exception) =>
        exception is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.SerializationConflict } or TerraBaseMigrationConflictException;
}

internal sealed class TerraBaseExecutionStrategyFactory(ExecutionStrategyDependencies dependencies) : RelationalExecutionStrategyFactory(dependencies)
{
    protected override IExecutionStrategy CreateDefaultStrategy(ExecutionStrategyDependencies dependencies) => new TerraBaseExecutionStrategy(dependencies);
}
