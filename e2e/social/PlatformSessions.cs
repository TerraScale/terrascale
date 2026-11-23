using System.Data.Common;
using TerraScale.DotNet.TerraBase;

namespace Journey.Social;

internal static class PlatformSessions
{
    private static bool Empty(string reading, string name) => reading.Split('\n').Any(line =>
        (line.StartsWith(name + "{", StringComparison.Ordinal) || line.StartsWith(name + " ", StringComparison.Ordinal))
        && line.EndsWith(" 0", StringComparison.Ordinal));

    public static async Task RunAsync()
    {
        var state = Environment.GetEnvironmentVariable("E2E_STATE") ?? throw new InvalidOperationException("E2E_STATE is required");
        var secret = (await File.ReadAllLinesAsync(Path.Combine(state, "secrets")))[0];
        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        http.DefaultRequestHeaders.Add("X-TerraBase-Peer-Credential", secret);
        var database = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { BaseAddress = new Uri("http://127.0.0.1:8081") })
            .Database("00000000000000000000000002", "ts-auth");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var connections = new List<TerraBaseDbConnection>();
        using var metrics = new HttpClient();
        try
        {
            for (var index = 0; index < 16; index++)
            {
                connections.Add(new TerraBaseDbConnection(database));
            }

            await Task.WhenAll(connections.Select(connection => TransactAsync(connection, metrics, deadline.Token)));
            Console.WriteLine("16 platform ADO.NET transactions completed");
            await Console.Out.FlushAsync(deadline.Token);
            _ = await Console.In.ReadLineAsync(deadline.Token);
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static async Task TransactAsync(DbConnection connection, HttpClient metrics, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 42";
        if (await command.ExecuteScalarAsync(cancellationToken) is not 42L)
        {
            throw new InvalidOperationException("platform transaction did not read 42");
        }

        var reading = await metrics.GetStringAsync(new Uri("http://127.0.0.1:9091/metrics"), cancellationToken);
        if (!Empty(reading, "terrabase_qos_active_sessions") || !Empty(reading, "terrabase_qos_preauthentication_sessions"))
        {
            throw new InvalidOperationException("platform transaction traffic entered customer session accounting");
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
