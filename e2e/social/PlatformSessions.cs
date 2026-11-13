using Microsoft.EntityFrameworkCore;
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
            .Database("00000000000000000000000002", "00000000000000000000000003");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var contexts = new List<DbContext>();
        using var metrics = new HttpClient();
        try
        {
            for (var index = 0; index < 16; index++)
            {
                contexts.Add(new DbContext(new DbContextOptionsBuilder().UseTerraBase(database).Options));
            }

            await Task.WhenAll(contexts.Select(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(deadline.Token);
                await context.Database.ExecuteSqlRawAsync("SELECT 42", deadline.Token);
                var reading = await metrics.GetStringAsync(new Uri("http://127.0.0.1:9091/metrics"), deadline.Token);
                if (!Empty(reading, "terrabase_qos_active_sessions") || !Empty(reading, "terrabase_qos_preauthentication_sessions"))
                {
                    throw new InvalidOperationException("platform EF traffic entered customer session accounting");
                }

                await transaction.CommitAsync(deadline.Token);
            }));

            Console.WriteLine("16 platform EF transactions completed");
            await Console.Out.FlushAsync(deadline.Token);
            _ = await Console.In.ReadLineAsync(deadline.Token);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }
}
