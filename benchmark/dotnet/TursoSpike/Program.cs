using System.Diagnostics;
using System.Runtime.InteropServices;
using T = Turso.Data.Sqlite;

var dir = Path.Combine(Path.GetTempPath(), "turso-probe-" + Environment.ProcessId);
Directory.CreateDirectory(dir);
var mode = args.FirstOrDefault() ?? "features";
try
{
    switch (mode)
    {
        case "features": Features.Run(dir); break;
        case "cdc": Cdc.Run(dir); break;
        case "bench": Bench.Run(dir, args.Skip(1).ToArray()); break;
        case "stress": Stress.Run(dir, args.Skip(1).ToArray()); break;
        case "designc": DesignC.Run(dir); break;
        case "wrap":
        {
            var key = new string('a', 64);
            var p = Path.Combine(dir, "wrap.db");
            Native.Open(p, "encryption,views,index_method", cipher: "aegis256", key: key);
            var tc = new Turso.TursoConnection($"Data Source={p};Encryption Cipher=aegis256;Encryption Key={key}");
            tc.Open();
            X.Check("facade wrapping encrypted TursoConnection: open", () => { using var f = new T.SqliteConnection(tc, false); if (f.State != System.Data.ConnectionState.Open) f.Open(); return f.State; });
            var w = new T.SqliteConnection(tc, false);
            if (w.State != System.Data.ConnectionState.Open) w.Open();
            X.Check("wrap: exec + materialized view", () => { w.Exec("CREATE TABLE d (id INTEGER PRIMARY KEY, c TEXT)"); w.Exec("CREATE MATERIALIZED VIEW mv AS SELECT c, count(*) n FROM d GROUP BY c"); w.Exec("INSERT INTO d (c) VALUES ('a')"); return w.Rows("SELECT * FROM mv"); });
            X.Check("wrap: CreateFunction", () => { w.CreateFunction("tb_twice", (long x) => x * 2); return w.Scalar("SELECT tb_twice(21)"); });
            X.Check("wrap: ADO savepoint", () => { using var tx = w.BeginTransaction(); tx.Save("s"); w.Exec("INSERT INTO d (c) VALUES ('b')"); tx.Rollback("s"); tx.Commit(); return w.Scalar("SELECT count(*) FROM d"); });
            X.Check("wrap: CDC", () => { w.Exec("PRAGMA capture_data_changes_conn('full')"); w.Exec("INSERT INTO d (c) VALUES ('z')"); return w.Rows("SELECT bin_record_json_object(table_columns_json_array('d'), after) FROM turso_cdc WHERE table_name = 'd'"); });
            break;
        }
        case "lattice":
        {
            var p = Path.Combine(dir, "lattice.db");
            using var c = X.Open(p);
            c.Exec("PRAGMA journal_mode = WAL");
            c.Exec("PRAGMA capture_data_changes_conn('full')");
            c.Exec("CREATE TABLE users (id TEXT PRIMARY KEY, name TEXT, age INTEGER, doc TEXT)");
            c.Exec("CREATE TABLE field_stamps (tbl TEXT, id TEXT, field TEXT, hlc INTEGER, PRIMARY KEY (tbl, id, field))");
            c.Exec("INSERT INTO users VALUES ('a','Ann',30,'{\"x\":1,\"y\":2}'),('b','Bob',20,'{\"x\":1}')");
            X.Check("user SQL write -> own CDC rows inside the open txn -> per-field stamps, one commit", () =>
            {
                c.Exec("BEGIN IMMEDIATE");
                var mark = (long)c.Scalar("SELECT coalesce(max(change_id), 0) FROM turso_cdc")!;
                c.Exec("UPDATE users SET name = upper(name), doc = json_set(doc, '$.y', 3) WHERE age > 25");   // arbitrary user SQL
                var rows = c.Rows($"SELECT change_type, id, bin_record_json_object(table_columns_json_array('users'), before), bin_record_json_object(table_columns_json_array('users'), after), bin_record_json_object(table_columns_json_array('users'), updates) FROM turso_cdc WHERE change_id > {mark} AND table_name = 'users'");
                foreach (var r in rows)
                {
                    var changed = System.Text.Json.JsonDocument.Parse((string)r[4]!).RootElement.EnumerateObject().Where(f => f.Value.GetInt32() == 1).Select(f => f.Name);
                    var after = System.Text.Json.JsonDocument.Parse((string)r[3]!).RootElement;
                    foreach (var f in changed) c.Exec("INSERT OR REPLACE INTO field_stamps VALUES ('users', $id, $f, 42)", ("$id", after.GetProperty("id").GetString()), ("$f", f));
                }
                c.Exec("COMMIT");
                return $"cdc rows in txn={rows.Count}; stamps=" + string.Join(" ", c.Rows("SELECT id || '.' || field FROM field_stamps").Select(r => r[0]));
            });
            break;
        }
        case "tc":
        {
            using var c = new Turso.TursoConnection($"Data Source={Path.Combine(dir, "tc.db")}");
            c.Open();
            int Exec(string sql, params (string, object)[] ps) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (n, v) in ps) cmd.Parameters.Add(new Turso.TursoParameter(n, v)); return cmd.ExecuteNonQuery(); }
            object? Scalar(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
            X.Check("tc: DDL + params + reader", () =>
            {
                Exec("CREATE TABLE d (id INTEGER PRIMARY KEY, k TEXT UNIQUE, v BLOB)");
                Exec("INSERT INTO d (k, v) VALUES ($k, $v)", ("$k", "a"), ("$v", new byte[] { 1, 2, 3 }));
                using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT k, v FROM d"; using var r = cmd.ExecuteReader(); r.Read();
                return $"{r.GetString(0)} {((byte[])r.GetValue(1)).Length}B";
            });
            X.Check("tc: SQL savepoints in BEGIN IMMEDIATE", () => { Exec("BEGIN IMMEDIATE"); Exec("SAVEPOINT s"); Exec("INSERT INTO d (k) VALUES ('b')"); Exec("ROLLBACK TO s"); Exec("RELEASE s"); Exec("COMMIT"); return Scalar("SELECT count(*) FROM d"); });
            X.Check("tc: ADO BeginTransaction rollback", () => { using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO d (k) VALUES ('c')"; cmd.ExecuteNonQuery(); tx.Rollback(); return Scalar("SELECT count(*) FROM d"); });
            X.Check("tc: multi-statement (no forward refs)", () => { var n = Exec("INSERT INTO d (k) VALUES ('x'); INSERT INTO d (k) VALUES ('y');"); return $"returned {n}, rows x/y={Scalar("SELECT count(*) FROM d WHERE k IN ('x','y')")}"; });
            X.Check("tc: constraint error code", () => { try { Exec("INSERT INTO d (k) VALUES ('a')"); return "no error"; } catch (Exception e) { return $"{e.GetType().FullName}: {e.Message}"; } });
            X.Check("tc: PRAGMA busy_timeout / journal_mode", () => { Exec("PRAGMA busy_timeout = 1000"); return Scalar("PRAGMA journal_mode"); });
            break;
        }
        case "uring":
            X.Check("io_uring vfs open + write", () =>
            {
                var p = Path.Combine(dir, "uring.db");
                Native.Open(p, "", vfs: "io_uring");
                using var c = X.Open(p);
                c.Exec("CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)");
                c.Exec("INSERT INTO t (v) VALUES ('x')");
                return c.Scalar("SELECT count(*) FROM t");
            });
            break;
        default: Console.WriteLine("usage: features | cdc | bench <seconds>"); break;
    }
}
finally
{
    if (mode != "keep") try { Directory.Delete(dir, true); } catch { }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    struct Config { public ulong AsyncIo; public nint Path, Features, Vfs, Cipher, HexKey, PageCodec; public uint OpenFlags; }

    [DllImport("turso_sdk_kit")] static extern int turso_database_new(ref Config c, out nint db, out nint err);
    [DllImport("turso_sdk_kit")] static extern int turso_database_open(nint db, out nint err);

    static readonly List<nint> Keep = [];

    // Opens a Database with experimental features; later SqliteConnection opens of the same path hit Turso's
    // process-wide registry and share this instance (and its feature set).
    public static void Open(string path, string features, string? vfs = null, string? cipher = null, string? key = null)
    {
        var p = Marshal.StringToCoTaskMemUTF8(path);
        var f = Marshal.StringToCoTaskMemUTF8(features);
        var v = vfs is null ? 0 : Marshal.StringToCoTaskMemUTF8(vfs);
        var c = cipher is null ? 0 : Marshal.StringToCoTaskMemUTF8(cipher);
        var k = key is null ? 0 : Marshal.StringToCoTaskMemUTF8(key);
        var cfg = new Config { Path = p, Features = f, Vfs = v, Cipher = c, HexKey = k };
        var rc = turso_database_new(ref cfg, out var db, out var err);
        if (rc != 0) throw new InvalidOperationException($"database_new rc={rc} {Marshal.PtrToStringUTF8(err)}");
        rc = turso_database_open(db, out err);
        if (rc != 0) throw new InvalidOperationException($"database_open rc={rc} {Marshal.PtrToStringUTF8(err)}");
        Keep.Add(db);
    }
}

static class X
{
    public static T.SqliteConnection Open(string path, string extra = "")
    {
        var c = new T.SqliteConnection($"Data Source={path}{extra}");
        c.Open();
        return c;
    }

    public static object? Scalar(this T.SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    public static int Exec(this T.SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static List<object?[]> Rows(this T.SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public static void Check(string name, Func<object?> body)
    {
        try
        {
            var result = body();
            Console.WriteLine($"OK    {name}{(result is null ? "" : " -> " + Fmt(result))}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAIL  {name}: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        }
    }

    static string Fmt(object o) => o is List<object?[]> rows
        ? string.Join(" | ", rows.Take(4).Select(r => string.Join(",", r.Select(v => v is byte[] b ? $"blob[{b.Length}]" : v?.ToString() ?? "NULL"))))
        : o.ToString() ?? "";
}

static class Features
{
    public static void Run(string dir)
    {
        Console.WriteLine($"native: {X.Open(":memory:").Scalar("SELECT sqlite_version()")}");
        var plain = Path.Combine(dir, "plain.db");
        using var c = X.Open(plain);

        Console.WriteLine("== plain open (no experimental features)");
        foreach (var p in new[]
                 {
                     "PRAGMA journal_mode = WAL", "PRAGMA synchronous = NORMAL", "PRAGMA synchronous", "PRAGMA user_version = 1",
                     "PRAGMA user_version", "PRAGMA query_only = 1", "PRAGMA query_only = 0", "PRAGMA temp_store = MEMORY",
                     "PRAGMA cache_size = -2000", "PRAGMA mmap_size = 0", "PRAGMA wal_autocheckpoint = 1000",
                     "PRAGMA busy_timeout = 5000", "PRAGMA schema_version", "PRAGMA foreign_keys = ON", "PRAGMA integrity_check",
                     "PRAGMA quick_check", "PRAGMA optimize", "PRAGMA wal_checkpoint(TRUNCATE)", "PRAGMA wal_checkpoint(PASSIVE)",
                     "PRAGMA page_size", "PRAGMA page_count", "PRAGMA freelist_count", "PRAGMA application_id = 42",
                     "PRAGMA locking_mode", "PRAGMA soft_heap_limit = 1000000", "PRAGMA hard_heap_limit = 1000000",
                     "PRAGMA journal_size_limit = 1000000", "PRAGMA auto_vacuum", "PRAGMA encoding", "PRAGMA data_version",
                 })
            X.Check(p, () => c.Rows(p));

        X.Check("CREATE TABLE STRICT", () => c.Exec("CREATE TABLE s (id INTEGER PRIMARY KEY, v TEXT NOT NULL) STRICT"));
        X.Check("CREATE TABLE WITHOUT ROWID", () => c.Exec("CREATE TABLE w (k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID"));
        X.Check("rowid docs table", () => c.Exec("CREATE TABLE d (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT, PRIMARY KEY (collection, id))"));
        X.Check("partial index", () => c.Exec("CREATE INDEX d_live ON d (collection, id) WHERE body IS NOT NULL"));
        X.Check("json_extract expression index", () => c.Exec("CREATE INDEX d_email ON d (collection, json_extract(body, '$.email')) WHERE body IS NOT NULL"));
        X.Check("upsert ON CONFLICT DO UPDATE ... RETURNING", () => c.Rows("INSERT INTO d VALUES ('u','1',1,'{\"email\":\"a@x\"}') ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body WHERE excluded.version > d.version RETURNING version"));
        X.Check("upsert conflict path", () => c.Rows("INSERT INTO d VALUES ('u','1',2,'{\"email\":\"b@x\"}') ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body WHERE excluded.version > d.version RETURNING version, body"));
        X.Check("expression index used (EXPLAIN QUERY PLAN)", () => c.Rows("EXPLAIN QUERY PLAN SELECT id FROM d WHERE collection = 'u' AND json_extract(body, '$.email') = 'b@x' AND body IS NOT NULL"));
        X.Check("json_each", () => c.Rows("SELECT key, value FROM json_each('{\"a\":1,\"b\":[2]}')"));
        X.Check("json_tree", () => c.Rows("SELECT fullkey FROM json_tree('{\"a\":{\"b\":1}}')"));
        X.Check("jsonb", () => c.Rows("SELECT json(jsonb('{\"a\":1}')), jsonb_extract(jsonb('{\"a\":1}'), '$.a')"));
        X.Check("json_patch / json_set", () => c.Rows("SELECT json_patch('{\"a\":1}','{\"b\":2}'), json_set('{}','$.x',1)"));
        X.Check("trigger", () =>
        {
            c.Exec("CREATE TABLE log (seq INTEGER PRIMARY KEY, id TEXT)");
            c.Exec("CREATE TRIGGER d_ins AFTER INSERT ON d BEGIN INSERT INTO log (id) VALUES (NEW.id); END");
            c.Exec("INSERT INTO d VALUES ('u','2',1,NULL)");
            return c.Scalar("SELECT count(*) FROM log");
        });
        X.Check("savepoint/rollback to/release", () =>
        {
            c.Exec("BEGIN IMMEDIATE");
            c.Exec("SAVEPOINT a");
            c.Exec("INSERT INTO d VALUES ('u','3',1,'{}')");
            c.Exec("ROLLBACK TO a");
            c.Exec("RELEASE a");
            c.Exec("COMMIT");
            return c.Scalar("SELECT count(*) FROM d WHERE id = '3'");
        });
        X.Check("ADO savepoint API", () =>
        {
            using var tx = c.BeginTransaction();
            tx.Save("x");
            c.Exec("INSERT INTO d VALUES ('u','4',1,'{}')");
            tx.Rollback("x");
            tx.Commit();
            return c.Scalar("SELECT count(*) FROM d WHERE id = '4'");
        });
        X.Check("BEGIN IMMEDIATE + busy on 2nd writer", () =>
        {
            using var c2 = X.Open(plain);
            c.Exec("BEGIN IMMEDIATE");
            try
            {
                c2.Exec("PRAGMA busy_timeout = 200");
                var sw = Stopwatch.StartNew();
                try { c2.Exec("BEGIN IMMEDIATE"); c2.Exec("ROLLBACK"); return "2nd writer NOT blocked"; }
                catch (Exception e) { return $"{e.GetType().Name} after {sw.ElapsedMilliseconds} ms: {e.Message}" + (e is T.SqliteException se ? $" code={se.SqliteErrorCode}" : ""); }
            }
            finally { c.Exec("ROLLBACK"); }
        });
        X.Check("reader sees snapshot while writer open", () =>
        {
            using var c2 = X.Open(plain);
            c.Exec("BEGIN IMMEDIATE");
            c.Exec("INSERT INTO d VALUES ('u','5',1,'{}')");
            var during = c2.Scalar("SELECT count(*) FROM d WHERE id='5'");
            c.Exec("COMMIT");
            return $"during={during} after={c2.Scalar("SELECT count(*) FROM d WHERE id='5'")}";
        });
        X.Check("trigger via Turso.TursoConnection (no facade splitter)", () =>
        {
            using var tc = new Turso.TursoConnection($"Data Source={plain}");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER d_ins2 AFTER INSERT ON d BEGIN INSERT INTO log (id) VALUES (NEW.id); END";
            cmd.ExecuteNonQuery();
            c.Exec("INSERT INTO d VALUES ('u','9',1,NULL)");
            return c.Scalar("SELECT count(*) FROM log");
        });
        X.Check("multi-statement CommandText (schema bootstrap style)", () => c.Exec("CREATE TABLE ms1 (a); CREATE TABLE ms2 (b); INSERT INTO ms1 VALUES (1);"));
        X.Check("expression index chosen when only filter", () => c.Rows("EXPLAIN QUERY PLAN SELECT id FROM d WHERE json_extract(body, '$.email') = 'b@x'"));
        X.Check("expression index chosen with INDEXED BY", () => c.Rows("EXPLAIN QUERY PLAN SELECT id FROM d INDEXED BY d_email WHERE collection = 'u' AND json_extract(body, '$.email') = 'b@x' AND body IS NOT NULL"));
        X.Check("CreateFunction (scalar UDF)", () => { c.CreateFunction("tb_twice", (long x) => x * 2); return c.Scalar("SELECT tb_twice(21)"); });
        X.Check("ANALYZE + sqlite_stat1", () => { c.Exec("ANALYZE"); return c.Rows("SELECT tbl, idx, stat FROM sqlite_stat1"); });
        X.Check("sqlite_schema", () => c.Scalar("SELECT count(*) FROM sqlite_schema"));
        X.Check("TEMP TABLE", () => c.Exec("CREATE TEMP TABLE tmp (id TEXT PRIMARY KEY)"));
        X.Check("ATTACH", () => c.Exec($"ATTACH '{Path.Combine(dir, "att.db")}' AS other"));
        X.Check("VACUUM INTO", () => c.Exec($"VACUUM INTO '{Path.Combine(dir, "copy.db")}'"));
        X.Check("BackupDatabase", () => { using var dst = X.Open(Path.Combine(dir, "bak.db")); c.BackupDatabase(dst); return dst.Scalar("SELECT count(*) FROM d"); });
        X.Check("Handle", () => c.Handle ?? "null");
        X.Check("SqliteCommand.Cancel interrupts long query", () =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM r LIMIT 30000000) SELECT count(*) FROM r";
            var sw = Stopwatch.StartNew();
            _ = Task.Run(async () => { await Task.Delay(300); cmd.Cancel(); });
            try { return $"not cancelled: finished {cmd.ExecuteScalar()} in {sw.ElapsedMilliseconds} ms"; }
            catch (Exception e) { return $"{e.GetType().Name} after {sw.ElapsedMilliseconds} ms: {e.Message}"; }
        });
        X.Check("CommandTimeout interrupts long query", () =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandTimeout = 1;
            cmd.CommandText = "WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM r LIMIT 30000000) SELECT count(*) FROM r";
            var sw = Stopwatch.StartNew();
            try { return $"finished {cmd.ExecuteScalar()} in {sw.ElapsedMilliseconds} ms"; }
            catch (Exception e) { return $"{e.GetType().Name} after {sw.ElapsedMilliseconds} ms: {e.Message}"; }
        });
        X.Check("ExecuteScalarAsync cancellation token", () =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM r LIMIT 30000000) SELECT count(*) FROM r";
            using var cts = new CancellationTokenSource(300);
            var sw = Stopwatch.StartNew();
            try { return $"finished {cmd.ExecuteScalarAsync(cts.Token).GetAwaiter().GetResult()} in {sw.ElapsedMilliseconds} ms"; }
            catch (Exception e) { return $"{e.GetType().Name} after {sw.ElapsedMilliseconds} ms: {e.Message}"; }
        });
        X.Check("vector functions (brute force)", () =>
        {
            c.Exec("CREATE TABLE v (id INTEGER PRIMARY KEY, e BLOB)");
            c.Exec("INSERT INTO v (e) VALUES (vector32('[1,0,0]')), (vector32('[0,1,0]')), (vector32('[0.9,0.1,0]'))");
            return c.Rows("SELECT id, vector_distance_cos(e, vector32('[1,0,0]')) d FROM v ORDER BY d LIMIT 2");
        });
        X.Check("CREATE MATERIALIZED VIEW without flag", () => c.Exec("CREATE MATERIALIZED VIEW mv AS SELECT collection, count(*) n FROM d GROUP BY collection"));
        X.Check("plain VIEW", () => { c.Exec("CREATE VIEW pv AS SELECT id FROM d"); return c.Scalar("SELECT count(*) FROM pv"); });

        Console.WriteLine("== registry trick: native open with experimental features, then provider opens same path");
        var feat = Path.Combine(dir, "feat.db");
        X.Check("native open views,index_method,without_rowid,vacuum,attach", () => { Native.Open(feat, "views,index_method,without_rowid,vacuum,attach,generated_columns"); return null; });
        using var f = X.Open(feat);
        X.Check("WITHOUT ROWID create", () => f.Exec("CREATE TABLE w (k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID"));
        X.Check("WITHOUT ROWID insert", () => f.Exec("INSERT INTO w VALUES ('a','1'),('b','2')"));
        X.Check("WITHOUT ROWID update", () => f.Exec("UPDATE w SET v = 'x' WHERE k = 'a'"));
        X.Check("WITHOUT ROWID upsert", () => f.Exec("INSERT INTO w VALUES ('a','y') ON CONFLICT (k) DO UPDATE SET v = excluded.v"));
        X.Check("WITHOUT ROWID delete", () => f.Exec("DELETE FROM w WHERE k = 'b'"));
        X.Check("WITHOUT ROWID secondary index", () => f.Exec("CREATE INDEX w_v ON w (v)"));
        X.Check("ATTACH (flag)", () => f.Exec($"ATTACH '{Path.Combine(dir, "att2.db")}' AS other"));
        X.Check("VACUUM INTO (flag)", () => f.Exec($"VACUUM INTO '{Path.Combine(dir, "copy2.db")}'"));
        X.Check("MATERIALIZED VIEW (DBSP)", () =>
        {
            f.Exec("CREATE TABLE d (id INTEGER PRIMARY KEY, collection TEXT, n INTEGER)");
            f.Exec("CREATE MATERIALIZED VIEW mv AS SELECT collection, count(*) c, sum(n) s FROM d GROUP BY collection");
            f.Exec("INSERT INTO d (collection, n) VALUES ('a',1),('a',2),('b',5)");
            f.Exec("UPDATE d SET n = 10 WHERE id = 1");
            f.Exec("DELETE FROM d WHERE id = 3");
            return f.Rows("SELECT * FROM mv ORDER BY collection");
        });
        X.Check("MATERIALIZED VIEW survives reopen in new connection", () => { using var g = X.Open(feat); return g.Rows("SELECT * FROM mv"); });
        X.Check("sparse vector index (toy_vector_sparse_ivf)", () =>
        {
            f.Exec("CREATE TABLE sv (id INTEGER PRIMARY KEY, e BLOB)");
            f.Exec("CREATE INDEX sv_idx ON sv USING toy_vector_sparse_ivf (e)");
            f.Exec("INSERT INTO sv (e) VALUES (vector32_sparse('[1,0,0,0]')), (vector32_sparse('[0,1,0,0]'))");
            return f.Rows("SELECT id, vector_distance_jaccard(e, vector32_sparse('[1,0,0,0]')) d FROM sv ORDER BY d LIMIT 1");
        });
        X.Check("FTS index", () => { f.Exec("CREATE TABLE art (id INTEGER PRIMARY KEY, title TEXT)"); return f.Exec("CREATE INDEX art_fts ON art USING fts (title)"); });
        X.Check("FTS5 virtual table", () => f.Exec("CREATE VIRTUAL TABLE ft USING fts5(title)"));

        Console.WriteLine("== encryption");
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var enc = Path.Combine(dir, "enc.db");
        X.Check("Turso.TursoConnection Encryption Cipher/Key + WAL + CDC", () =>
        {
            using var e = new Turso.TursoConnection($"Data Source={enc};Encryption Cipher=aegis256;Encryption Key={key}");
            e.Open();
            foreach (var sql in new[] { "PRAGMA journal_mode = WAL", "PRAGMA capture_data_changes_conn('full')", "CREATE TABLE t (id INTEGER PRIMARY KEY, secret TEXT)", "INSERT INTO t (secret) VALUES ('plaintext-marker-123')" })
            { using var cmd = e.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            using var q = e.CreateCommand();
            q.CommandText = "SELECT bin_record_json_object(table_columns_json_array('t'), after) FROM turso_cdc WHERE table_name = 't'";
            return q.ExecuteScalar();
        });
        X.Check("plaintext absent from db+wal files", () =>
        {
            var hits = Directory.GetFiles(dir, "enc.db*").Where(p => File.ReadAllText(p).Contains("plaintext-marker-123")).ToArray();
            return hits.Length == 0 ? "absent in " + string.Join(",", Directory.GetFiles(dir, "enc.db*").Select(Path.GetFileName)) : "FOUND in " + string.Join(",", hits.Select(Path.GetFileName));
        });
        X.Check("facade open of encrypted file (no key) after TursoConnection closed", () => { using var e = X.Open(enc); return e.Scalar("SELECT secret FROM t"); });
        X.Check("TursoConnection wrong key", () =>
        {
            using var e = new Turso.TursoConnection($"Data Source={enc};Encryption Cipher=aegis256;Encryption Key={new string('0', 64)}");
            e.Open(); using var q = e.CreateCommand(); q.CommandText = "SELECT secret FROM t"; return q.ExecuteScalar();
        });
        var enc2 = Path.Combine(dir, "enc2.db");
        X.Check("native open encryption+views, facade conns share it", () =>
        {
            Native.Open(enc2, "encryption,views,index_method", cipher: "aegis256", key: key);
            using var e = X.Open(enc2);
            e.Exec("PRAGMA capture_data_changes_conn('full')");
            e.Exec("CREATE TABLE d (id INTEGER PRIMARY KEY, c TEXT)");
            e.Exec("CREATE MATERIALIZED VIEW mv AS SELECT c, count(*) n FROM d GROUP BY c");
            e.Exec("INSERT INTO d (c) VALUES ('secret-marker-456'),('secret-marker-456')");
            e.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
            var hits = Directory.GetFiles(dir, "enc2.db*").Count(p => File.ReadAllText(p).Contains("secret-marker-456"));
            return $"{string.Join(",", e.Rows("SELECT * FROM mv")[0])} plaintext-files={hits}";
        });
        X.Check("native open encryption+views, then TursoConnection with same key shares it", () =>
        {
            var p = Path.Combine(dir, "enc3.db");
            Native.Open(p, "encryption,views,index_method", cipher: "aegis256", key: key);
            using var e = new Turso.TursoConnection($"Data Source={p};Encryption Cipher=aegis256;Encryption Key={key}");
            e.Open();
            foreach (var sql in new[] { "PRAGMA capture_data_changes_conn('full')", "CREATE TABLE d (id INTEGER PRIMARY KEY, c TEXT)", "CREATE MATERIALIZED VIEW mv AS SELECT c, count(*) n FROM d GROUP BY c", "INSERT INTO d (c) VALUES ('secret-marker-456'),('secret-marker-456')", "PRAGMA wal_checkpoint(TRUNCATE)" })
            { using var cmd = e.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            using var q = e.CreateCommand(); q.CommandText = "SELECT c || ':' || n FROM mv";
            var hits = Directory.GetFiles(dir, "enc3.db*").Count(f => File.ReadAllText(f).Contains("secret-marker-456"));
            return $"{q.ExecuteScalar()} plaintext-files={hits}";
        });
        X.Check("TursoConnection: multi-statement + trigger + savepoint", () =>
        {
            using var e = new Turso.TursoConnection($"Data Source={Path.Combine(dir, "tc.db")}");
            e.Open();
            using var cmd = e.CreateCommand();
            cmd.CommandText = "CREATE TABLE a (id INTEGER PRIMARY KEY, v TEXT); CREATE TABLE l (id INTEGER PRIMARY KEY, v TEXT); CREATE TRIGGER a_i AFTER INSERT ON a BEGIN INSERT INTO l (v) VALUES (NEW.v); END; INSERT INTO a (v) VALUES ('x');";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "BEGIN; SAVEPOINT s; INSERT INTO a (v) VALUES ('y'); ROLLBACK TO s; RELEASE s; COMMIT;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT (SELECT count(*) FROM a) || '/' || (SELECT count(*) FROM l)";
            return cmd.ExecuteScalar();
        });

        Console.WriteLine("== MVCC");
        var mv = Path.Combine(dir, "mvcc.db");
        using var m = X.Open(mv);
        X.Check("PRAGMA journal_mode = mvcc", () => m.Rows("PRAGMA journal_mode = mvcc"));
        X.Check("BEGIN CONCURRENT x2 disjoint rows", () =>
        {
            m.Exec("CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)");
            using var m2 = X.Open(mv);
            m.Exec("BEGIN CONCURRENT"); m2.Exec("BEGIN CONCURRENT");
            m.Exec("INSERT INTO t VALUES (1,'a')"); m2.Exec("INSERT INTO t VALUES (2,'b')");
            m.Exec("COMMIT"); m2.Exec("COMMIT");
            return m.Scalar("SELECT count(*) FROM t");
        });
        X.Check("BEGIN CONCURRENT x2 same row (conflict)", () =>
        {
            using var m2 = X.Open(mv);
            m.Exec("BEGIN CONCURRENT"); m2.Exec("BEGIN CONCURRENT");
            m.Exec("UPDATE t SET v = 'x' WHERE id = 1");
            try { m2.Exec("UPDATE t SET v = 'y' WHERE id = 1"); m.Exec("COMMIT"); m2.Exec("COMMIT"); return "no conflict?"; }
            catch (Exception e) { try { m.Exec("COMMIT"); } catch { } try { m2.Exec("ROLLBACK"); } catch { } return $"{e.GetType().Name}: {e.Message}"; }
        });
        X.Check("MVCC + CDC captures?", () =>
        {
            using var m3 = X.Open(mv);
            m3.Exec("PRAGMA capture_data_changes_conn('full')");
            m3.Exec("BEGIN CONCURRENT"); m3.Exec("INSERT INTO t VALUES (10,'cdc')"); m3.Exec("COMMIT");
            m3.Exec("INSERT INTO t VALUES (11,'cdc-auto')");
            return m3.Rows("SELECT change_id, change_type, table_name, id FROM turso_cdc ORDER BY change_id");
        });
        X.Check("MVCC + CDC, 2 threads BEGIN CONCURRENT x 2000 each", () =>
        {
            var p = Path.Combine(dir, "mvcccdc.db");
            using (var i = X.Open(p)) { i.Exec("PRAGMA journal_mode = mvcc"); i.Exec("PRAGMA capture_data_changes_conn('full')"); i.Exec("CREATE TABLE t (id INTEGER PRIMARY KEY, w INTEGER, v TEXT)"); }
            long pragmaRetries = 0;
            long conflicts = 0;
            var ts = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
            {
                using var c = X.Open(p);
                for (;;) { try { c.Exec("PRAGMA capture_data_changes_conn('full')"); break; } catch { Interlocked.Increment(ref pragmaRetries); Thread.Sleep(1); } }
                for (var n = 0; n < 2000; n++)
                    for (;;)
                    {
                        try { c.Exec("BEGIN CONCURRENT"); c.Exec("INSERT INTO t (w, v) VALUES ($w, 'x')", ("$w", w)); c.Exec("COMMIT"); break; }
                        catch { Interlocked.Increment(ref conflicts); try { c.Exec("ROLLBACK"); } catch { } }
                    }
            })).ToArray();
            Task.WaitAll(ts);
            using var r = X.Open(p);
            return string.Join(",", r.Rows("SELECT count(*), count(DISTINCT change_id), sum(change_type=1), sum(change_type=2), max(change_id) FROM turso_cdc")[0]) + $" rows={r.Scalar("SELECT count(*) FROM t")} retries={conflicts} pragma-retries={pragmaRetries}"
                + " txn-groups-interleaved=" + r.Scalar("SELECT count(*) FROM (SELECT change_txn_id, max(change_id) - min(change_id) + 1 span, count(*) n FROM turso_cdc GROUP BY change_txn_id) WHERE span <> n");
        });
        X.Check("WAL CDC db then switch to mvcc", () =>
        {
            using var w = X.Open(Path.Combine(dir, "walcdc.db"));
            w.Exec("PRAGMA capture_data_changes_conn('full')");
            w.Exec("CREATE TABLE t (id INTEGER PRIMARY KEY)");
            return w.Rows("PRAGMA journal_mode = mvcc");
        });
    }
}

static class Cdc
{
    public static void Run(string dir)
    {
        var path = Path.Combine(dir, "cdc.db");
        using var w = X.Open(path);
        w.Exec("PRAGMA journal_mode = WAL");
        w.Exec("PRAGMA capture_data_changes_conn('full')");
        w.Exec("CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT, PRIMARY KEY (collection, id))");
        w.Exec("INSERT INTO documents VALUES ('c','a',1,'{\"x\":1}')");
        w.Exec("INSERT INTO documents VALUES ('c','a',2,'{\"x\":2}') ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body");
        w.Exec("BEGIN"); w.Exec("INSERT INTO documents VALUES ('c','b',1,NULL)"); w.Exec("DELETE FROM documents WHERE id = 'a'"); w.Exec("COMMIT");
        Console.WriteLine("turso_cdc schema: " + w.Scalar("SELECT sql FROM sqlite_schema WHERE name = 'turso_cdc'"));
        Console.WriteLine("turso_cdc indexes: " + string.Join("; ", w.Rows("SELECT name, sql FROM sqlite_schema WHERE tbl_name = 'turso_cdc' AND type = 'index'").Select(r => $"{r[0]} {r[1]}")));
        foreach (var r in w.Rows("""
            SELECT change_id, change_txn_id, change_type, table_name, id,
                   bin_record_json_object(table_columns_json_array('documents'), before),
                   bin_record_json_object(table_columns_json_array('documents'), after),
                   updates IS NOT NULL
            FROM turso_cdc WHERE table_name IN ('documents') OR change_type = 2 ORDER BY change_id
            """))
            Console.WriteLine("  " + string.Join(" | ", r.Select(v => v?.ToString() ?? "NULL")));
        X.Check("plain UPDATE before/after", () =>
        {
            w.Exec("INSERT INTO documents VALUES ('c','u',1,'{\"v\":1}')");
            w.Exec("UPDATE documents SET version = 2, body = '{\"v\":2}' WHERE id = 'u'");
            return w.Rows("SELECT bin_record_json_object(table_columns_json_array('documents'), before), bin_record_json_object(table_columns_json_array('documents'), after), typeof(updates) FROM turso_cdc WHERE change_type = 0 ORDER BY change_id DESC LIMIT 1");
        });
        X.Check("INSERT OR REPLACE capture", () =>
        {
            w.Exec("INSERT OR REPLACE INTO documents VALUES ('c','u',3,'{\"v\":3}')");
            return w.Rows("SELECT change_type, bin_record_json_object(table_columns_json_array('documents'), before), bin_record_json_object(table_columns_json_array('documents'), after) FROM turso_cdc WHERE table_name = 'documents' ORDER BY change_id DESC LIMIT 2");
        });
        X.Check("updates column decode", () => w.Rows("SELECT change_id, typeof(updates), length(updates), bin_record_json_object(table_columns_json_array('documents'), updates) FROM turso_cdc WHERE change_type = 0"));

        X.Check("second connection without CDC pragma is NOT captured", () =>
        {
            using var o = X.Open(path);
            var before = w.Scalar("SELECT max(change_id) FROM turso_cdc");
            o.Exec("INSERT INTO documents VALUES ('c','z',1,'{}')");
            return $"max change_id before={before} after={w.Scalar("SELECT max(change_id) FROM turso_cdc")}";
        });
        X.Check("CDC on 2 writer connections: change_id unique+monotonic", () =>
        {
            using var w2 = X.Open(path);
            w2.Exec("PRAGMA capture_data_changes_conn('full')");
            for (var i = 0; i < 50; i++) { w.Exec($"INSERT INTO documents VALUES ('m','w1-{i}',1,'{{}}')"); w2.Exec($"INSERT INTO documents VALUES ('m','w2-{i}',1,'{{}}')"); }
            return w.Rows("SELECT count(*), count(DISTINCT change_id), min(change_id), max(change_id) FROM turso_cdc WHERE table_name = 'documents'");
        });
        X.Check("rolled-back txn leaves no CDC rows", () =>
        {
            var before = w.Scalar("SELECT count(*) FROM turso_cdc");
            w.Exec("BEGIN"); w.Exec("INSERT INTO documents VALUES ('r','1',1,'{}')"); w.Exec("ROLLBACK");
            return $"before={before} after={w.Scalar("SELECT count(*) FROM turso_cdc")}";
        });
        X.Check("truncate consumed rows (DELETE ... WHERE change_id <= ?)", () =>
        {
            var max = (long)w.Scalar("SELECT max(change_id) FROM turso_cdc")!;
            using var o = X.Open(path);
            var n = o.Exec("DELETE FROM turso_cdc WHERE change_id <= $m", ("$m", max - 5));
            w.Exec("INSERT INTO documents VALUES ('c','after-trunc',1,'{}')");
            return $"deleted={n} remaining={w.Scalar("SELECT count(*) FROM turso_cdc")} next_id={w.Scalar("SELECT max(change_id) FROM turso_cdc")} (was {max})";
        });
        X.Check("change_id after truncating everything + reopen", () =>
        {
            var max = (long)w.Scalar("SELECT max(change_id) FROM turso_cdc")!;
            w.Exec("DELETE FROM turso_cdc");
            using var w3 = X.Open(path);
            w3.Exec("PRAGMA capture_data_changes_conn('full')");
            w3.Exec("INSERT INTO documents VALUES ('c','after-empty',1,'{}')");
            return $"old max={max} new={w3.Scalar("SELECT min(change_id) FROM turso_cdc")}";
        });

        // Poller concurrent with 2 writer threads.
        X.Check("poller tails turso_cdc while 2 writers run (3 s)", () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            long written = 0;
            var writers = Enumerable.Range(0, 2).Select(t => Task.Run(() =>
            {
                using var c = X.Open(path);
                c.Exec("PRAGMA busy_timeout = 5000");
                for (;;) { try { c.Exec("PRAGMA capture_data_changes_conn('full')"); break; } catch { Thread.Sleep(1); } }
                var i = 0;
                while (!cts.IsCancellationRequested)
                {
                    try { c.Exec("INSERT INTO documents VALUES ('p',$id,1,'{\"n\":1}')", ("$id", $"t{t}-{i++}")); Interlocked.Increment(ref written); }
                    catch (Exception e) { Console.WriteLine($"   writer {t}: {e.Message}"); Thread.Sleep(1); }
                }
            })).ToArray();
            long seen = 0, last = (long)w.Scalar("SELECT coalesce(max(change_id),0) FROM turso_cdc")!, polls = 0, gaps = 0;
            using var r = X.Open(path);
            var sw = Stopwatch.StartNew();
            while (!cts.IsCancellationRequested || writers.Any(x => !x.IsCompleted))
            {
                var rows = r.Rows($"SELECT change_id, change_type, bin_record_json_object(table_columns_json_array('documents'), after) FROM turso_cdc WHERE change_id > {last} ORDER BY change_id LIMIT 1000");
                polls++;
                foreach (var row in rows)
                {
                    var id = (long)row[0]!;
                    if (id != last + 1) gaps++;
                    last = id;
                    if ((long)row[1]! == 1) seen++;
                }
                if (rows.Count == 0) Thread.Sleep(5);
                if (writers.All(x => x.IsCompleted) && rows.Count == 0) break;
            }
            Task.WaitAll(writers);
            return $"written={written} seen={seen} polls={polls} non-contiguous-steps={gaps} in {sw.ElapsedMilliseconds} ms";
        });
    }
}

static class Bench
{
    // TerraBase-style upsert: documents row (state blob + version + body) per write, one write per transaction.
    const string Schema = """
        CREATE TABLE IF NOT EXISTS documents (
            collection TEXT NOT NULL, id TEXT NOT NULL, state BLOB NOT NULL, version INTEGER NOT NULL, body TEXT,
            PRIMARY KEY (collection, id));
        CREATE INDEX IF NOT EXISTS documents_live ON documents (collection, id) WHERE body IS NOT NULL;
        """;
    const string Upsert = """
        INSERT INTO documents (collection, id, state, version, body) VALUES ($c, $id, $state, $v, $body)
        ON CONFLICT (collection, id) DO UPDATE SET state = excluded.state, version = excluded.version, body = excluded.body
        WHERE excluded.version > documents.version
        """;

    // Design C shape (docs/terrabase/multi-region-replication.md section 5): per write, the collection row and the
    // _tb_state row, plain INSERT/UPDATE (no ON CONFLICT), TEXT only (bin_record_json_object cannot format BLOBs).
    const string DesignCSchema = """
        CREATE TABLE IF NOT EXISTS users (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS _tb_state (collection TEXT NOT NULL, id TEXT NOT NULL, bucket INTEGER NOT NULL, state TEXT NOT NULL,
            stamp INTEGER NOT NULL, origin INTEGER NOT NULL, live INTEGER NOT NULL, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL, UNIQUE (collection, id));
        CREATE INDEX IF NOT EXISTS _tb_state_bucket ON _tb_state (bucket, collection, id)
        """;

    static readonly string[] Engines =
    [
        "ms-wal-normal",
        "turso-wal", "turso-wal-cdc", "turso-wal-cdc-after",
        "turso-wal-tc", "turso-wal-cdc-tc", "turso-wal-cdc-after-tc",
        "turso-wal-uring", "turso-wal-cdc-uring", "turso-wal-cdc-after-uring",
        "turso-wal-aegis256", "turso-wal-cdc-aegis256", "turso-wal-cdc-after-aegis256",
        "turso-mvcc-concurrent", "turso-mvcc-concurrent-cdc",
        "ms-wal-normal-again",
    ];

    // bench <seconds> [engine-prefix|all] [upsert|designc|both] [writers: 1|2|both]
    public static void Run(string dir, string[] a)
    {
        var seconds = a.Length > 0 ? int.Parse(a[0]) : 5;
        var only = a.Length > 1 && a[1] != "all" ? a[1] : null;
        var workloads = a.Length > 2 && a[2] != "both" ? [a[2]] : new[] { "upsert", "designc" };
        var writerCounts = a.Length > 3 && a[3] != "both" ? [int.Parse(a[3])] : new[] { 1, 2 };
        var body = "{\"name\":\"" + new string('x', 180) + "\",\"n\":12345,\"tags\":[\"a\",\"b\",\"c\"]}";
        var state = new byte[256];
        Random.Shared.NextBytes(state);
        Console.WriteLine($"cpus={Environment.ProcessorCount} seconds={seconds} body={body.Length}B state={state.Length}B; one write per txn, 50% new ids / 50% updates");
        Console.WriteLine("upsert = 1 row per txn via ON CONFLICT DO UPDATE (BLOB state); designc = collection row + _tb_state row per txn, plain INSERT/UPDATE, TEXT state");
        foreach (var workload in workloads)
        {
            Console.WriteLine();
            Console.WriteLine($"workload {workload}");
            Console.WriteLine("| engine | writers | writes/s | p50 ms | p99 ms | p99.9 ms | max ms | db+wal MB | cdc data rows | cdc rows/write |");
            Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var writers in writerCounts)
            foreach (var engine in Engines)
            {
                if (only is not null && !engine.StartsWith(only)) continue;
                var path = Path.Combine(dir, $"{workload}-{engine}-{writers}.db");
                try { RunOne(engine, workload, path, writers, seconds, body, state); }
                catch (Exception e) { Console.WriteLine($"| {engine} | {writers} | FAIL {e.GetType().Name}: {e.Message.Split('\n')[0]} | | | | | | | |"); }
                foreach (var f in Directory.GetFiles(dir, Path.GetFileName(path) + "*")) File.Delete(f);
            }
        }
        if (File.Exists("/proc/self/status")) Console.WriteLine(string.Join(" ", File.ReadAllLines("/proc/self/status").Where(l => l.StartsWith("VmRSS") || l.StartsWith("VmHWM")).Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+", " "))));
    }

    static void RunOne(string engine, string workload, string path, int writers, int seconds, string body, byte[] state)
    {
        var ms = engine.StartsWith("ms");
        var mvcc = engine.Contains("mvcc");
        var aegis = engine.Contains("aegis");
        var uring = engine.Contains("uring");
        // Capture is on for every engine whose name contains "-cdc"; "-cdc-after" selects mode after.
        var cdc = engine.Contains("-cdc-after") ? "after" : engine.Contains("-cdc") ? "full" : null;
        var key = new string('7', 64);
        Func<System.Data.Common.DbConnection> open = ms
            ? () => { var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); return c; }
            : aegis
                ? () => { var c = new Turso.TursoConnection($"Data Source={path};Encryption Cipher=aegis256;Encryption Key={key}"); c.Open(); return c; }
                : engine.EndsWith("-tc")
                    ? () => { var c = new Turso.TursoConnection($"Data Source={path}"); c.Open(); return c; }
                    : () => { var c = new T.SqliteConnection($"Data Source={path}"); c.Open(); return c; };
        if (uring) Native.Open(path, "", vfs: "io_uring");
        using (var init = open())
        {
            Exec(init, mvcc ? "PRAGMA journal_mode = mvcc" : "PRAGMA journal_mode = WAL");
            Exec(init, "PRAGMA synchronous = NORMAL");
            if (cdc is not null) Exec(init, $"PRAGMA capture_data_changes_conn('{cdc}')");
            foreach (var s in (workload == "designc" ? DesignCSchema : Schema).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Exec(init, s);
        }
        long total = 0, busy = 0;
        var lat = new List<double>[writers];
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var stateText = Convert.ToBase64String(state).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var threads = Enumerable.Range(0, writers).Select(t =>
        {
            lat[t] = new List<double>(1 << 20);
            var th = new Thread(() =>
            {
                using var c = open();
                Exec(c, "PRAGMA synchronous = NORMAL");
                Exec(c, "PRAGMA busy_timeout = 10000");
                if (cdc is not null) for (;;) { try { Exec(c, $"PRAGMA capture_data_changes_conn('{cdc}')"); break; } catch { Thread.Sleep(1); } }
                using var begin = c.CreateCommand(); begin.CommandText = mvcc ? "BEGIN CONCURRENT" : "BEGIN IMMEDIATE";
                using var commit = c.CreateCommand(); commit.CommandText = "COMMIT";
                Action<string, long, bool> write;
                if (workload == "designc")
                {
                    var updDoc = c.CreateCommand(); updDoc.CommandText = "UPDATE users SET doc = $doc WHERE id = $id";
                    var insDoc = c.CreateCommand(); insDoc.CommandText = "INSERT INTO users (id, doc) VALUES ($id, $doc)";
                    var updState = c.CreateCommand(); updState.CommandText = "UPDATE _tb_state SET state = $s, stamp = $v, origin = 1, live = 1, h1 = $v, h2 = -$v WHERE collection = 'users' AND id = $id";
                    var insState = c.CreateCommand(); insState.CommandText = "INSERT INTO _tb_state (collection, id, bucket, state, stamp, origin, live, h1, h2) VALUES ('users', $id, $b, $s, $v, 1, 1, $v, -$v)";
                    write = (id, v, existing) =>
                    {
                        foreach (var cmd in existing ? new[] { updDoc, updState } : [insDoc, insState])
                        {
                            cmd.Parameters.Clear();
                            Param(cmd, "$id", id);
                            if (cmd == updDoc || cmd == insDoc) Param(cmd, "$doc", body);
                            else { Param(cmd, "$s", stateText); Param(cmd, "$v", v); if (cmd == insState) Param(cmd, "$b", (long)(id.GetHashCode() & 4095)); }
                            cmd.ExecuteNonQuery();
                        }
                    };
                }
                else
                {
                    var cmd = c.CreateCommand();
                    cmd.CommandText = Upsert;
                    Param(cmd, "$c", "users"); var pid = Param(cmd, "$id", ""); Param(cmd, "$state", state);
                    var pv = Param(cmd, "$v", 0L); Param(cmd, "$body", body);
                    write = (id, v, _) => { pid.Value = id; pv.Value = v; cmd.ExecuteNonQuery(); };
                }
                var rnd = new Random(t);
                long i = 0;
                var sw = new Stopwatch();
                while (!stop.IsCancellationRequested)
                {
                    // Half the writes update an existing id of this writer, half insert a new one.
                    var existing = i > 0 && rnd.Next(2) == 0;
                    var n = existing ? rnd.NextInt64(i) : i++;
                    sw.Restart();
                    try
                    {
                        begin.ExecuteNonQuery(); write($"{t:D2}-{n:D12}", Environment.TickCount64 * 16 + t, existing); commit.ExecuteNonQuery();
                        lat[t].Add(sw.Elapsed.TotalMilliseconds);
                        Interlocked.Increment(ref total);
                    }
                    catch (System.Data.Common.DbException)
                    {
                        Interlocked.Increment(ref busy);
                        try { Exec(c, "ROLLBACK"); } catch { }
                    }
                }
            }) { IsBackground = true };
            th.Start();
            return th;
        }).ToArray();
        var clock = Stopwatch.StartNew();
        foreach (var th in threads) th.Join();
        var elapsed = clock.Elapsed.TotalSeconds;
        var all = lat.SelectMany(l => l).OrderBy(x => x).ToArray();
        var size = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*").Sum(f => new FileInfo(f).Length) / 1048576.0;
        // Proof that capture happened: data rows (types 1, 0, -1) in turso_cdc for the benchmark tables.
        string cdcRows = "-", perWrite = "-";
        if (!ms)
        {
            using var check = open();
            using var q = check.CreateCommand();
            q.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name = 'turso_cdc'";
            if (Convert.ToInt64(q.ExecuteScalar()) == 1)
            {
                q.CommandText = "SELECT count(*) FROM turso_cdc WHERE change_type IN (-1, 0, 1) AND table_name <> 'sqlite_schema'";
                var n = Convert.ToInt64(q.ExecuteScalar());
                cdcRows = n.ToString();
                perWrite = total == 0 ? "-" : (n / (double)total).ToString("F2");
            }
            else cdcRows = "no table";
        }
        Console.WriteLine($"| {engine} | {writers} | {total / elapsed:F0} | {Pct(all, 0.5):F3} | {Pct(all, 0.99):F3} | {Pct(all, 0.999):F1} | {(all.Length > 0 ? all[^1] : 0):F0} | {size:F1} | {cdcRows} | {perWrite} |{(busy > 0 ? $" retries={busy}" : "")}");
    }

    static double Pct(double[] s, double p) => s.Length == 0 ? 0 : s[(int)Math.Min(s.Length - 1, s.Length * p)];

    static System.Data.Common.DbParameter Param(System.Data.Common.DbCommand cmd, string name, object v)
    {
        var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = v; cmd.Parameters.Add(p); return p;
    }

    static void Exec(System.Data.Common.DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
}

static class Stress
{
    // 1 writer (WAL + CDC) + 2 point readers + 1 CDC poller on one file; checks the process survives and the poller sees every write.
    public static void Run(string dir, string[] a)
    {
        var seconds = a.Length > 0 ? int.Parse(a[0]) : 20;
        var mvcc = a.Length > 1 && a[1] == "mvcc";
        var writers = a.Length > 2 ? int.Parse(a[2]) : 1;
        var path = Path.Combine(dir, "stress.db");
        using (var i = X.Open(path))
        {
            i.Exec(mvcc ? "PRAGMA journal_mode = mvcc" : "PRAGMA journal_mode = WAL");
            i.Exec("PRAGMA capture_data_changes_conn('full')");
            i.Exec("CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT, PRIMARY KEY (collection, id))");
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        long written = 0, reads = 0, seen = 0, errors = 0;
        var tasks = new List<Thread>();
        void Start(Action a) { var t = new Thread(() => { try { a(); } catch (Exception e) { Interlocked.Increment(ref errors); Console.WriteLine("thread: " + e.Message); } }) { IsBackground = true }; t.Start(); tasks.Add(t); }
        for (var w = 0; w < writers; w++)
        {
            var wi = w;
            Start(() =>
            {
                using var c = X.Open(path);
                c.Exec("PRAGMA busy_timeout = 10000");
                for (;;) { try { c.Exec("PRAGMA capture_data_changes_conn('full')"); break; } catch { Thread.Sleep(1); } }
                var n = 0;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        c.Exec(mvcc ? "BEGIN CONCURRENT" : "BEGIN IMMEDIATE");
                        c.Exec("INSERT INTO documents VALUES ('c', $id, $v, '{\"n\":1}') ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body", ("$id", $"{wi}-{n++ % 5000}"), ("$v", n));
                        c.Exec("COMMIT");
                        Interlocked.Increment(ref written);
                    }
                    catch { Interlocked.Increment(ref errors); try { c.Exec("ROLLBACK"); } catch { } }
                }
            });
        }
        for (var r = 0; r < 2; r++)
            Start(() =>
            {
                using var c = X.Open(path);
                var rnd = new Random();
                while (!stop.IsCancellationRequested)
                {
                    c.Scalar("SELECT body FROM documents WHERE collection = 'c' AND id = $id", ("$id", $"0-{rnd.Next(5000)}"));
                    Interlocked.Increment(ref reads);
                }
            });
        Start(() =>
        {
            using var c = X.Open(path);
            long last = 0;
            while (!stop.IsCancellationRequested)
            {
                var rows = c.Rows($"SELECT change_id, change_type FROM turso_cdc WHERE change_id > {last} ORDER BY change_id LIMIT 1000");
                foreach (var row in rows) { last = (long)row[0]!; if ((long)row[1]! is 0 or 1 && last > 2) Interlocked.Increment(ref seen); }
                if (rows.Count == 0) Thread.Sleep(2);
                else if (last > 20000) c.Exec("DELETE FROM turso_cdc WHERE change_id < $m", ("$m", last - 1000));
            }
        });
        foreach (var t in tasks) t.Join();
        Console.WriteLine($"survived mvcc={mvcc} writers={writers}: written={written} reads={reads} cdc-seen={seen} errors={errors} wal-MB={(File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length / 1048576.0 : 0):F1}");
    }
}

// The four Turso behaviours design C depends on (docs/terrabase/multi-region-replication.md), through both providers.
static class DesignC
{
    const string Schema = """
        CREATE TABLE users (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
        CREATE TABLE _tb_state (collection TEXT NOT NULL, id TEXT NOT NULL, bucket INTEGER NOT NULL, state TEXT NOT NULL,
            stamp INTEGER NOT NULL, origin INTEGER NOT NULL, live INTEGER NOT NULL, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL, UNIQUE (collection, id));
        CREATE INDEX _tb_state_bucket ON _tb_state (bucket, collection, id);
        CREATE TABLE _tb_tail (id INTEGER PRIMARY KEY CHECK (id = 1), change_id INTEGER NOT NULL);
        INSERT INTO _tb_tail VALUES (1, 0)
        """;

    sealed record Db(System.Data.Common.DbConnection C) : IDisposable
    {
        public void Dispose() => C.Dispose();
        public int Exec(string sql, params (string, object?)[] ps)
        {
            using var cmd = C.CreateCommand(); cmd.CommandText = sql;
            foreach (var (n, v) in ps) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v ?? DBNull.Value; cmd.Parameters.Add(p); }
            return cmd.ExecuteNonQuery();
        }
        public object? Scalar(string sql) { using var cmd = C.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
        public List<object?[]> Rows(string sql)
        {
            using var cmd = C.CreateCommand(); cmd.CommandText = sql; using var r = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            while (r.Read()) { var row = new object?[r.FieldCount]; for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i); rows.Add(row); }
            return rows;
        }
        public long Max => Convert.ToInt64(Scalar("SELECT coalesce(max(change_id), 0) FROM turso_cdc"));
        public string Since(long mark) => string.Join(" | ", Rows($"SELECT change_type, coalesce(table_name, '-'), before IS NOT NULL, after IS NOT NULL, updates IS NOT NULL FROM turso_cdc WHERE change_id > {mark} ORDER BY change_id")
            .Select(r => r[0] is 2L ? "COMMIT" : $"{r[1]}:{r[0] switch { 1L => "ins", 0L => "upd", -1L => "del", _ => "?" }}(b={r[2]},a={r[3]},u={r[4]})"));
    }

    public static void Run(string dir)
    {
        foreach (var provider in new[] { "TursoConnection", "facade" })
        {
            Console.WriteLine($"== provider {provider}");
            var path = Path.Combine(dir, $"designc-{provider}.db");
            System.Data.Common.DbConnection Open() { System.Data.Common.DbConnection c = provider == "facade" ? new T.SqliteConnection($"Data Source={path}") : new Turso.TursoConnection($"Data Source={path}"); c.Open(); return c; }
            using var w = new Db(Open());
            w.Exec("PRAGMA journal_mode = WAL");
            foreach (var stmt in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) w.Exec(stmt);

            // 1. Mode after.
            X.Check("1 after: enable", () => w.Rows("PRAGMA capture_data_changes_conn('after')").FirstOrDefault()?.FirstOrDefault());
            X.Check("1 after: insert / update / delete images", () =>
            {
                var m = w.Max;
                w.Exec("INSERT INTO users VALUES ('a', '{\"n\":1}')");
                w.Exec("UPDATE users SET doc = '{\"n\":2}' WHERE id = 'a'");
                w.Exec("DELETE FROM users WHERE id = 'a'");
                return w.Since(m);
            });
            X.Check("1 after: decoded update after-image", () => w.Rows("SELECT id, bin_record_json_object(table_columns_json_array('users'), after) FROM turso_cdc WHERE table_name = 'users' AND change_type = 0").Last()[1]);
            X.Check("1 after: delete row carries only the rowid", () => string.Join(",", w.Rows("SELECT id, typeof(before), typeof(after) FROM turso_cdc WHERE table_name = 'users' AND change_type = -1").Last()));
            X.Check("1 full vs after: bytes per update (doc 2 KB)", () =>
            {
                var big = "{\"x\":\"" + new string('y', 2000) + "\"}";
                w.Exec("INSERT INTO users VALUES ('big', $d)", ("$d", big));
                long Bytes(string mode)
                {
                    w.Exec($"PRAGMA capture_data_changes_conn('{mode}')");
                    var m = w.Max;
                    w.Exec("UPDATE users SET doc = $d WHERE id = 'big'", ("$d", big.Replace('y', mode[0])));
                    return Convert.ToInt64(w.Scalar($"SELECT sum(coalesce(length(before),0) + coalesce(length(after),0) + coalesce(length(updates),0)) FROM turso_cdc WHERE change_id > {m}"));
                }
                var full = Bytes("full"); var after = Bytes("after");
                return $"full={full} B, after={after} B";
            });

            // 2. Toggle per transaction.
            X.Check("2 toggle: off before BEGIN, on after COMMIT", () =>
            {
                w.Exec("PRAGMA capture_data_changes_conn('after')");
                var m = w.Max;
                w.Exec("PRAGMA capture_data_changes_conn('off')");
                w.Exec("BEGIN IMMEDIATE"); w.Exec("UPDATE _tb_tail SET change_id = change_id + 1"); w.Exec("INSERT INTO users VALUES ('off1', '{}')"); w.Exec("COMMIT");
                w.Exec("PRAGMA capture_data_changes_conn('after')");
                w.Exec("INSERT INTO users VALUES ('on1', '{}')");
                return w.Since(m);
            });
            X.Check("2 toggle: inside BEGIN IMMEDIATE (off ... after ... COMMIT)", () =>
            {
                var m = w.Max;
                w.Exec("BEGIN IMMEDIATE");
                try
                {
                    w.Exec("PRAGMA capture_data_changes_conn('off')");
                    w.Exec("INSERT INTO users VALUES ('off2', '{}')");
                    w.Exec("PRAGMA capture_data_changes_conn('after')");
                    w.Exec("INSERT INTO users VALUES ('on2', '{}')");
                    w.Exec("COMMIT");
                }
                catch { try { w.Exec("ROLLBACK"); } catch { } throw; }
                return w.Since(m);
            });
            X.Check("2 toggle: DELETE FROM turso_cdc while off, newest row kept", () =>
            {
                var max = w.Max;
                w.Exec("PRAGMA capture_data_changes_conn('off')");
                w.Exec("BEGIN IMMEDIATE");
                var n = w.Exec($"DELETE FROM turso_cdc WHERE change_id < {max}");
                w.Exec($"UPDATE _tb_tail SET change_id = {max}");
                w.Exec("COMMIT");
                w.Exec("PRAGMA capture_data_changes_conn('after')");
                w.Exec("INSERT INTO users VALUES ('after-trunc', '{}')");
                return $"deleted={n}, remaining ids={string.Join(",", w.Rows("SELECT change_id FROM turso_cdc ORDER BY change_id").Select(r => r[0]))}";
            });
            X.Check("2 toggle: cost of an off/on pair (1000 pairs)", () =>
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 1000; i++) { w.Exec("PRAGMA capture_data_changes_conn('off')"); w.Exec("PRAGMA capture_data_changes_conn('after')"); }
                return $"{sw.Elapsed.TotalMilliseconds / 1000:F3} ms per pair";
            });
            X.Check("2 toggle: while a reader connection holds a read txn", () =>
            {
                using var r = new Db(Open());
                r.Exec("BEGIN"); r.Scalar("SELECT count(*) FROM users");
                try { w.Exec("PRAGMA capture_data_changes_conn('off')"); w.Exec("PRAGMA capture_data_changes_conn('after')"); return "ok"; }
                finally { r.Exec("COMMIT"); }
            });

            // 3. bin_record_json_object on real design-C rows.
            X.Check("3 decode: _tb_state + collection row round-trip exactly", () =>
            {
                w.Exec("PRAGMA capture_data_changes_conn('after')");
                var bytes = new byte[3000]; Random.Shared.NextBytes(bytes); var state = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var doc = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["name"] = "Zoë 😀 漢字 \"quoted\" back\\slash\nnew\u0001ctl", ["n"] = 9007199254740993L, ["f"] = 0.1, ["nested"] = new { a = new[] { 1, 2 } }, ["nil"] = null,
                    ["big"] = new string('z', 300_000),
                });
                var cases = new (long stamp, long h1, long h2)[] { (long.MaxValue, long.MinValue, -1), (1, 0, long.MaxValue) };
                var mismatches = new List<string>();
                var k = 0;
                foreach (var (stamp, h1, h2) in cases)
                {
                    var id = $"doc-{k++}";
                    var m = w.Max;
                    w.Exec("BEGIN IMMEDIATE");
                    w.Exec("INSERT INTO users (id, doc) VALUES ($id, $doc)", ("$id", id), ("$doc", doc));
                    w.Exec("INSERT INTO _tb_state VALUES ('users', $id, 4095, $s, $st, 7, 1, $h1, $h2)", ("$id", id), ("$s", state), ("$st", stamp), ("$h1", h1), ("$h2", h2));
                    w.Exec("COMMIT");
                    foreach (var row in w.Rows($"SELECT table_name, bin_record_json_object(table_columns_json_array(table_name), after) FROM turso_cdc WHERE change_id > {m} AND change_type = 1"))
                    {
                        using var j = System.Text.Json.JsonDocument.Parse((string)row[1]!);
                        var e = j.RootElement;
                        if ((string)row[0]! == "users")
                        {
                            if (e.GetProperty("id").GetString() != id) mismatches.Add("users.id");
                            if (e.GetProperty("doc").GetString() != doc) mismatches.Add("users.doc");
                        }
                        else
                        {
                            if (e.GetProperty("state").GetString() != state) mismatches.Add("state");
                            if (e.GetProperty("stamp").GetInt64() != stamp) mismatches.Add($"stamp {e.GetProperty("stamp").GetRawText()}");
                            if (e.GetProperty("h1").GetInt64() != h1) mismatches.Add($"h1 {e.GetProperty("h1").GetRawText()}");
                            if (e.GetProperty("h2").GetInt64() != h2) mismatches.Add($"h2 {e.GetProperty("h2").GetRawText()}");
                            if (e.GetProperty("bucket").GetInt64() != 4095 || e.GetProperty("origin").GetInt64() != 7 || e.GetProperty("live").GetInt64() != 1) mismatches.Add("ints");
                        }
                    }
                }
                return mismatches.Count == 0 ? $"exact (doc {doc.Length} chars, state {state.Length} chars, int64 extremes)" : "MISMATCH " + string.Join(", ", mismatches);
            });
            X.Check("3 decode: REAL and NULL columns", () =>
            {
                w.Exec("CREATE TABLE IF NOT EXISTS misc (id INTEGER PRIMARY KEY, r REAL, t TEXT)");
                var m = w.Max;
                w.Exec("INSERT INTO misc (r, t) VALUES (0.1, NULL)");
                return w.Scalar($"SELECT bin_record_json_object(table_columns_json_array('misc'), after) FROM turso_cdc WHERE change_id > {m} AND change_type = 1");
            });
            X.Check("3 decode: BLOB column", () =>
            {
                w.Exec("CREATE TABLE IF NOT EXISTS blobs (id INTEGER PRIMARY KEY, b BLOB)");
                var m = w.Max;
                w.Exec("INSERT INTO blobs (b) VALUES (x'00ff10')");
                return w.Scalar($"SELECT bin_record_json_object(table_columns_json_array('blobs'), after) FROM turso_cdc WHERE change_id > {m} AND change_type = 1");
            });
            X.Check("3 decode: 4096-row page with one query", () =>
            {
                w.Exec("BEGIN IMMEDIATE");
                for (var i = 0; i < 4096; i++) w.Exec("INSERT INTO users VALUES ($id, '{\"i\":1}')", ("$id", $"page-{i}"));
                w.Exec("COMMIT");
                var sw = Stopwatch.StartNew();
                var rows = w.Rows($"SELECT change_id, change_txn_id, change_type, table_name, bin_record_json_object(table_columns_json_array(table_name), after) FROM turso_cdc WHERE change_id > {w.Max - 4097} ORDER BY change_id LIMIT 4097");
                return $"{rows.Count} rows decoded in {sw.ElapsedMilliseconds} ms";
            });
            X.Check("3 decode: page with COMMIT rows via CASE guard", () =>
            {
                var sw = Stopwatch.StartNew();
                var rows = w.Rows($"SELECT change_id, change_txn_id, change_type, table_name, CASE WHEN change_type IN (0, 1) THEN bin_record_json_object(table_columns_json_array(table_name), after) END FROM turso_cdc WHERE change_id > {w.Max - 4097} ORDER BY change_id LIMIT 4097");
                return $"{rows.Count} rows ({rows.Count(r => r[4] is not null)} decoded, {rows.Count(r => r[2] is 2L)} commits) in {sw.ElapsedMilliseconds} ms";
            });
            X.Check("3 decode: bin_record_json_object(cols, NULL) on a delete row", () => w.Scalar("SELECT coalesce(bin_record_json_object(table_columns_json_array(table_name), after), 'NULL') FROM turso_cdc WHERE change_type = -1 AND table_name = 'users' LIMIT 1"));
            w.Exec("PRAGMA capture_data_changes_conn('off')");
        }

        // 4. Planner and expression indexes on a per-collection table (no overlapping partial index).
        Console.WriteLine("== planner: per-collection table, 20k docs");
        var pp = Path.Combine(dir, "planner.db");
        using var q = new Db(new Turso.TursoConnection($"Data Source={pp}"));
        q.C.Open();
        q.Exec("CREATE TABLE \"users\" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL)");
        q.Exec("BEGIN IMMEDIATE");
        var rnd = new Random(1);
        for (var i = 0; i < 20000; i++)
            q.Exec("INSERT INTO users VALUES ($id, $doc)", ("$id", $"u{i:D6}"), ("$doc", $"{{\"email\":\"user{i}@x.io\",\"org\":\"org{i % 50}\",\"age\":{rnd.Next(18, 90)},\"name\":\"n{i}\"}}"));
        q.Exec("COMMIT");
        q.Exec("CREATE INDEX \"_tb_ix_email\" ON \"users\" (json_extract(doc, '$.email'))");
        q.Exec("CREATE INDEX \"_tb_ix_org_age\" ON \"users\" (json_extract(doc, '$.org'), json_extract(doc, '$.age'))");
        var queries = new (string name, string sql)[]
        {
            ("eq literal", "SELECT id FROM users WHERE json_extract(doc, '$.email') = 'user77@x.io'"),
            ("eq parameter", "SELECT id FROM users WHERE json_extract(doc, '$.email') = ?1"),
            ("IN list", "SELECT id FROM users WHERE json_extract(doc, '$.email') IN ('user1@x.io', 'user2@x.io')"),
            ("range", "SELECT id FROM users WHERE json_extract(doc, '$.email') > 'user9'"),
            ("prefix LIKE", "SELECT id FROM users WHERE json_extract(doc, '$.email') LIKE 'user9%'"),
            ("ORDER BY expr LIMIT", "SELECT id FROM users ORDER BY json_extract(doc, '$.email') LIMIT 10"),
            ("composite eq + range", "SELECT id FROM users WHERE json_extract(doc, '$.org') = 'org7' AND json_extract(doc, '$.age') > 60"),
            ("composite leading column only", "SELECT id FROM users WHERE json_extract(doc, '$.org') = 'org7'"),
            ("composite eq + ORDER BY second", "SELECT id FROM users WHERE json_extract(doc, '$.org') = 'org7' ORDER BY json_extract(doc, '$.age') LIMIT 5"),
            ("composite second column only", "SELECT id FROM users WHERE json_extract(doc, '$.age') = 30"),
            ("eq + unindexed filter", "SELECT id FROM users WHERE json_extract(doc, '$.email') = 'user77@x.io' AND json_extract(doc, '$.name') = 'n77'"),
            ("->> spelling", "SELECT id FROM users WHERE doc ->> '$.email' = 'user77@x.io'"),
            ("id lookup", "SELECT doc FROM users WHERE id = 'u000077'"),
            ("OR of equalities", "SELECT id FROM users WHERE json_extract(doc, '$.email') = 'user1@x.io' OR json_extract(doc, '$.email') = 'user2@x.io'"),
            ("UNION ALL of equalities", "SELECT id FROM users WHERE json_extract(doc, '$.email') = 'user1@x.io' UNION ALL SELECT id FROM users WHERE json_extract(doc, '$.email') = 'user2@x.io'"),
            ("IN (json_each param)", "SELECT id FROM users WHERE json_extract(doc, '$.email') IN (SELECT value FROM json_each('[\"user1@x.io\",\"user2@x.io\"]'))"),
            ("prefix as range", "SELECT id FROM users WHERE json_extract(doc, '$.email') >= 'user9' AND json_extract(doc, '$.email') < 'user:'"),
            ("count(*) with expr eq", "SELECT count(*) FROM users WHERE json_extract(doc, '$.org') = 'org7'"),
        };
        foreach (var analyzed in new[] { false, true })
        {
            if (analyzed) q.Exec("ANALYZE");
            Console.WriteLine($"-- {(analyzed ? "after ANALYZE" : "before ANALYZE")}");
            foreach (var (name, sql) in queries)
                X.Check($"4 plan {name}", () =>
                {
                    var plan = string.Join(" / ", q.Rows("EXPLAIN QUERY PLAN " + sql.Replace("?1", "'user77@x.io'")).Select(r => r[^1]));
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < 20; i++) q.Rows(sql.Replace("?1", "'user77@x.io'"));
                    return $"{plan}  [{sw.Elapsed.TotalMilliseconds / 20:F2} ms]";
                });
        }
    }
}
