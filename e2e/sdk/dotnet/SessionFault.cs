using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.E2E;

internal static class SessionFault
{
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var origin = Required("E2E_SESSION_PROXY");
        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        var client = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY"),
            AuthAddress = new Uri("https://auth.terrascale.tech"),
            BaseAddress = new Uri(origin),
        });
        var db = client.Database(Required("TERRASCALE_PROJECT_ID"), Required("TERRASCALE_DATABASE_NAME"));
        var table = Required("E2E_SESSION_TABLE");
        var mode = Environment.GetEnvironmentVariable("E2E_SESSION_FAULT");
        await db.ExecuteAsync($"CREATE TABLE {table} (n INTEGER)", cancellationToken: cancellationToken);
        try
        {
            await using var session = await db.ConnectAsync(cancellationToken);
            if (mode is not ("autocommit" or "batch"))
            {
                await session.BeginAsync(cancellationToken);
                await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: cancellationToken);
            }
            if (mode == "before")
            {
                using var cut = await http.PostAsync(new Uri(new Uri(origin), "/cut"), null, cancellationToken);
                cut.EnsureSuccessStatusCode();
                await FailureAsync(() => session.QueryAsync("SELECT 1", cancellationToken: cancellationToken));
            }
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var operation = FailureAsync(async () =>
            {
                if (mode == "autocommit") await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: cancellationToken);
                else if (mode == "batch") await session.BatchAsync([new($"INSERT INTO {table} VALUES (1)"), new($"INSERT INTO {table} VALUES (2)")], cancellationToken);
                else if (mode == "read") await session.QueryAsync("SELECT 1", cancellationToken: cancellationToken);
                else await session.CommitAsync(caller.Token);
            });
            if (mode == "cancel")
            {
                using var committed = await http.PostAsync(new Uri(new Uri(origin), "/committed"), null, cancellationToken);
                committed.EnsureSuccessStatusCode();
                await caller.CancelAsync();
            }
            var first = await operation;
            var next = await FailureAsync(() => session.BeginAsync(cancellationToken));
            var second = await FailureAsync(() => session.ExecuteAsync("SELECT 2", cancellationToken: cancellationToken));
            if (mode is not ("before" or "read") && (first != "write_outcome_unknown" || next != first || second != first))
                throw new InvalidOperationException("The session did not retain its uncertain outcome.");
            var rows = await db.QueryAsync($"SELECT count(*) AS n FROM {table}", cancellationToken: cancellationToken);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("first", first);
                writer.WriteString("next", next);
                writer.WriteString("second", second);
                writer.WriteString("rows", rows[0].GetInt64("n").ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE {table}", cancellationToken: cancellationToken);
        }
        return 0;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name);

    private static async Task<string> FailureAsync(Func<Task> work)
    {
        try { await work(); return "accepted"; }
        catch (WebSocketException) { return "transport"; }
        catch (TerraBaseSqlException error) { return error.Code; }
        catch (TerraBaseException error) when (error.ErrorCode is not null) { return error.ErrorCode; }
    }
}
