using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace TerraScale.EntityFrameworkCore.Storage;

internal sealed class TerraBaseDatabaseCreator(RelationalDatabaseCreatorDependencies dependencies, IRawSqlCommandBuilder rawSqlCommandBuilder)
    : RelationalDatabaseCreator(dependencies)
{
    private const string HasTablesSql = """SELECT COUNT(*) FROM "sqlite_master" WHERE "type" = 'table' AND "rootpage" IS NOT NULL;""";

    public override bool Exists() => true;

    public override Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public override void Create() => throw Lifecycle();

    public override Task CreateAsync(CancellationToken cancellationToken = default) => throw Lifecycle();

    public override void Delete() => throw Lifecycle();

    public override Task DeleteAsync(CancellationToken cancellationToken = default) => throw Lifecycle();

    public override bool HasTables() => throw new NotSupportedException("TerraBase is async-only; use EnsureCreatedAsync.");

    public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default) =>
        await rawSqlCommandBuilder.Build(HasTablesSql).ExecuteScalarAsync(
            new RelationalCommandParameterObject(Dependencies.Connection, null, null, Dependencies.CurrentContext.Context, Dependencies.CommandLogger, CommandSource.Migrations),
            cancellationToken).ConfigureAwait(false) is long count && count != 0;

    private static NotSupportedException Lifecycle() =>
        new("TerraBase databases are created and deleted through the TerraBase database API, not through Entity Framework.");
}
