// Smoke test: Npgsql against TerraBase over the Postgres wire. See ../README.md.
using System.Security.Cryptography;
using Npgsql;

var url = Environment.GetEnvironmentVariable("TERRABASE_PG_URL");
if (string.IsNullOrEmpty(url))
{
    Console.WriteLine("set TERRABASE_PG_URL");
    return 2;
}

// Npgsql takes keyword connection strings, not URLs.
var uri = new Uri(url);
var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
var userInfo = uri.UserInfo.Split(':', 2);
var csb = new NpgsqlConnectionStringBuilder
{
    Host = uri.Host,
    Port = uri.Port > 0 ? uri.Port : 5432,
    Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
    Username = Uri.UnescapeDataString(userInfo[0]),
    Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
    SslMode = Enum.Parse<SslMode>(query["sslmode"] ?? "require", ignoreCase: true),
};
Console.WriteLine($"target {csb.Host}:{csb.Port} db={csb.Database}");

var t = $"smoke_cs_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
var owners = $"{t}_owners";
var items = $"{t}_items";
var insert = $"INSERT INTO {items} (sku, owner_id, qty) VALUES ($1, $2, $3)";

await using var conn = new NpgsqlConnection(csb.ConnectionString);
try
{
    await conn.OpenAsync();
}
catch (Exception e) when (e is NpgsqlException or TimeoutException)
{
    Console.WriteLine($"FAIL connect: {e.Message}");
    return 1;
}

NpgsqlCommand Cmd(string sql, params object[] args)
{
    var cmd = new NpgsqlCommand(sql, conn);
    foreach (var a in args)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = a });
    }
    return cmd;
}
async Task<int> Exec(string sql, params object[] args)
{
    await using var cmd = Cmd(sql, args);
    return await cmd.ExecuteNonQueryAsync();
}
async Task<long> Count() => (long)(await Cmd($"SELECT count(*) FROM {items}").ExecuteScalarAsync())!;
static void Check(bool ok, string what)
{
    if (!ok)
    {
        throw new InvalidOperationException($"check failed: {what}");
    }
}

(string Name, Func<Task> Run)[] steps =
[
    ("create tables (integer and text primary keys)", async () =>
    {
        await Exec($"CREATE TABLE {owners} (id integer PRIMARY KEY, name text NOT NULL)");
        await Exec($"CREATE TABLE {items} (sku text PRIMARY KEY, owner_id integer NOT NULL, qty integer)");
    }),
    // Npgsql always uses the extended protocol; this step sends no parameters.
    ("insert (literals, no parameters)", async () =>
    {
        var n = await Exec($"INSERT INTO {owners} (id, name) VALUES (1, 'ann'), (2, 'bob')");
        Check(n == 2, $"affected={n}, want 2");
        await Exec($"INSERT INTO {items} (sku, owner_id, qty) VALUES ('a-1', 1, 3)");
    }),
    ("insert with parameters (extended protocol, unnamed statement)", async () =>
    {
        var n = await Exec(insert, "a-2", 1, 5);
        Check(n == 1, $"affected={n}, want 1");
    }),
    ("insert with a named prepared statement, reused", async () =>
    {
        await using var cmd = Cmd(insert, "", 0, 0);
        await cmd.PrepareAsync();
        foreach (var (sku, owner, qty) in new[] { ("b-1", 2, 7), ("b-2", 2, 9) })
        {
            cmd.Parameters[0].Value = sku;
            cmd.Parameters[1].Value = owner;
            cmd.Parameters[2].Value = qty;
            var n = await cmd.ExecuteNonQueryAsync();
            Check(n == 1, $"affected={n}, want 1");
        }
        Check(await Count() == 4, "4 items");
    }),
    ("update with affected-row count", async () =>
    {
        var n = await Exec($"UPDATE {items} SET qty = qty + 1 WHERE owner_id = $1", 2);
        Check(n == 2, $"affected={n}, want 2");
    }),
    ("delete with affected-row count", async () =>
    {
        var n = await Exec($"DELETE FROM {items} WHERE sku = $1", "b-2");
        Check(n == 1, $"affected={n}, want 1");
        Check(await Count() == 3, "3 items");
    }),
    ("select with a join and a parameter", async () =>
    {
        await using var cmd = Cmd($"SELECT o.name, i.sku, i.qty FROM {items} i JOIN {owners} o ON o.id = i.owner_id WHERE o.name = $1 ORDER BY i.sku", "ann");
        await using var r = await cmd.ExecuteReaderAsync();
        var got = new List<string>();
        while (await r.ReadAsync())
        {
            got.Add($"{r.GetString(0)}/{r.GetString(1)}/{r.GetInt32(2)}");
        }
        Check(string.Join(",", got) == "ann/a-1/3,ann/a-2/5", $"rows={string.Join(",", got)}");
    }),
    ("transaction commit persists", async () =>
    {
        await using var tx = await conn.BeginTransactionAsync();
        await Exec(insert, "tx-1", 2, 1);
        await tx.CommitAsync();
        Check(await Count() == 4, "committed row visible");
    }),
    ("transaction rollback discards", async () =>
    {
        await using var tx = await conn.BeginTransactionAsync();
        await Exec(insert, "tx-2", 2, 1);
        await tx.RollbackAsync();
        Check(await Count() == 4, "rolled back row not visible");
    }),
    ("unique violation is SQLSTATE 23505", async () =>
    {
        try
        {
            await Exec(insert, "a-1", 1, 1);
        }
        catch (PostgresException e)
        {
            Check(e.SqlState == PostgresErrorCodes.UniqueViolation, $"sqlstate={e.SqlState}");
            return;
        }
        throw new InvalidOperationException("duplicate insert succeeded");
    }),
    ("prepared select reused", async () =>
    {
        await using var cmd = Cmd($"SELECT qty FROM {items} WHERE sku = $1", "");
        await cmd.PrepareAsync();
        foreach (var (sku, want) in new[] { ("a-1", 3), ("b-1", 8) })
        {
            cmd.Parameters[0].Value = sku;
            var qty = (int)(await cmd.ExecuteScalarAsync())!;
            Check(qty == want, $"{sku} qty={qty}, want {want}");
        }
    }),
];

var failed = false;
foreach (var (name, run) in steps)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception e)
    {
        failed = true;
        Console.WriteLine($"FAIL {name}: {e.Message}");
        break;
    }
}
try
{
    await Exec($"DROP TABLE IF EXISTS {items}");
    await Exec($"DROP TABLE IF EXISTS {owners}");
    Console.WriteLine("PASS drop tables");
}
catch (Exception e)
{
    failed = true;
    Console.WriteLine($"FAIL drop tables: {e.Message}");
}
Console.WriteLine(failed ? "FAIL" : "PASS");
return failed ? 1 : 0;
