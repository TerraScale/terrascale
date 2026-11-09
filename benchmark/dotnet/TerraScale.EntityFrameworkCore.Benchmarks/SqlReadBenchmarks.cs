using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet.TerraBase;
using TerraScale.EntityFrameworkCore.Tests;

namespace TerraScale.EntityFrameworkCore.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 3, iterationCount: 10)]
public class SqlReadBenchmarks : IDisposable
{
    private const string Sql = """SELECT "a"."Id", "a"."Avatar", "a"."CreatedAt", "a"."Email", "a"."Status", "a"."Verified", "a"."Version", "a"."Preferences" FROM "Accounts" AS "a" """;
    private HttpClient? http;
    private TerraBaseSqlDatabase? database;
    private DbContextOptions<AppDbContext>? options;
    private PooledDbContextFactory<AppDbContext>? pool;

    [Params(1, 1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var body = new StringBuilder("""{"results":[{"columns":[{"name":"Id","decltype":"TEXT"},{"name":"Avatar","decltype":"BLOB"},{"name":"CreatedAt","decltype":"TEXT"},{"name":"Email","decltype":"TEXT"},{"name":"Status","decltype":"INTEGER"},{"name":"Verified","decltype":"INTEGER"},{"name":"Version","decltype":"TEXT"},{"name":"Preferences","decltype":"TEXT"}],"rows":[""");
        for (var row = 0; row < Rows; row++)
        {
            body.Append(row == 0 ? "" : ",").Append(CultureInfo.InvariantCulture,
                $$"""["01KA{{row:D22}}",{"base64":"AQIDBAUGBwg="},"2026-11-09 10:30:15.125-03:00","user{{row}}@example.com",{{row % 3}},1,"01KB{{row:D22}}","{\"Theme\":\"dark\",\"PageSize\":50}"]""");
        }

        body.Append("""],"rows_affected":0,"last_insert_rowid":null}]}""");
        http = new HttpClient(new CannedSql(Encoding.UTF8.GetBytes(body.ToString())));
        database = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { BaseAddress = new Uri("http://127.0.0.1:9/"), AccessTokenProvider = _ => ValueTask.FromResult("token") })
            .Database("01KA0000000000000000000000", "app");
        options = new DbContextOptionsBuilder<AppDbContext>().UseTerraBase(database).Options;
        pool = new PooledDbContextFactory<AppDbContext>(options);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        http?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark]
    public async Task<long> Records()
    {
        var total = 0L;
        foreach (var row in await database!.QueryAsync(Sql))
        {
            total += row.GetString(0).Length + row.GetBytes(1).Length + row.GetString(2).Length + row.GetString(3).Length + row.GetInt64(4)
                + (row.GetBoolean(5) ? 1 : 0) + row.GetString(6).Length + row.GetString(7).Length;
        }

        return total;
    }

    [Benchmark]
    public async Task<long> DataReader()
    {
        await using var connection = new TerraBaseDbConnection(database!);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        var total = 0L;
        while (await reader.ReadAsync())
        {
            total += reader.GetString(0).Length + reader.GetFieldValue<byte[]>(1).Length + reader.GetFieldValue<DateTimeOffset>(2).Second + reader.GetString(3).Length
                + reader.GetInt32(4) + (reader.GetBoolean(5) ? 1 : 0) + reader.GetString(6).Length + reader.GetString(7).Length;
        }

        return total;
    }

    [Benchmark]
    public async Task<int> EntityFramework()
    {
        await using var context = new AppDbContext(options!);
        return (await context.Accounts.AsNoTracking().ToListAsync()).Count;
    }

    [Benchmark]
    public async Task<int> EntityFrameworkPooled()
    {
        await using var context = await pool!.CreateDbContextAsync();
        return (await context.Accounts.AsNoTracking().ToListAsync()).Count;
    }

    private sealed class CannedSql(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } } });
    }
}
