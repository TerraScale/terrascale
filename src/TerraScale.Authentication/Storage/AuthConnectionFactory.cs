using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Storage;

public sealed class AuthConnectionFactory(TerraBaseSqlDatabase database)
{
    public async Task<TerraBaseDbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new TerraBaseDbConnection(database);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
