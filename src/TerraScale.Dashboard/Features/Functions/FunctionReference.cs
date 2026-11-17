namespace TerraScale.Dashboard.Features.Functions
{
    internal static class FunctionReference
    {
        internal const string Text = """
            TerraScale Functions runtime reference

            Deploy a single JavaScript ES2023 script defining function handler(event), usually async.
            Do not use exports, imports, fetch, require, CLR, files, network or environment access.
            JavaScript runs in strict mode: declare variables with const, let or var; assigning an undeclared variable throws.
            SQL uses the SQLite dialect: use ? or named parameters, not PostgreSQL syntax.
            Each invocation has a fresh Jint engine. Only SQL on this function's database and console are available.
            Call deploy_function(databaseId, name, source, cron?) to create or replace, then invoke_function(databaseId, name, body?).
            Deploy requires SQL write access (TerraBase requires sql:read and sql:write); invoke requires sql:read, even when the handler writes SQL.
            Use databaseId from list_databases or get_context; pass organizationId and projectId explicitly when selecting a scope.
            Deployment returns name, cron, updated_at and the HTTPS invoke_url. Syntax failures return error.code, error.message and line/column when present in the runner message.
            invoke_function returns status, content_type, body (JSON value for application/json, otherwise text), log and log_note.
            Logs require SQL write access. Otherwise log is null and log_note explains the permission.
            If post-invocation log retrieval fails, status/body still describe the completed invocation; log is null and log_note explains the failure. Do not invoke again to retrieve logs.
            The log is the newest record after the call; concurrent HTTP or cron invocations may produce a newer log.

            Events:
            HTTP: {trigger: "http", body: <JSON value or raw text; null for an empty request>, query: {key: "value"}}
            MCP invoke_function body is a JSON value, default null. event.query is empty through MCP and the dashboard test run.
            Direct POST to invoke_url fills event.query from URL query parameters. Direct HTTPS requires a project bearer token with sql:read.
            Cron: {trigger: "cron", scheduledAt: "<UTC timestamp>"}. Cron events have no body or query.
            cron is a five-field UTC Cronos expression (minute hour day-of-month month day-of-week), at most once per minute.
            Example: "*/5 * * * *". Omit cron when deploying to remove the schedule; HTTP remains available with a cron schedule.
            Missed cron runs while the scheduling node is down are skipped.

            SQL signatures:
            await sql.execute(text, args?) -> {columns: string[], rows: JSON[][], rowsAffected: number, lastInsertRowid: number|null}
            await sql.query(text, args?) -> Array<Record<string, JSON>>
            await sql.batch(statements) -> Array<{columns: string[], rows: JSON[][], rowsAffected: number, lastInsertRowid: number|null}>
            await sql.transaction(async tx => value) -> value; commits when the callback returns; rolls back when it throws.
            await tx.execute(text, args?) -> same result as sql.execute
            await tx.query(text, args?) -> same result as sql.query
            await tx.batch(statements) -> same result as sql.batch
            tx has no transaction method. Do not nest transactions or issue BEGIN/COMMIT/ROLLBACK yourself.
            execute/query also accept a statement object: {sql: string, args?: JSON[], named?: Record<string, JSON>}.
            batch takes an array of those statement objects and runs them atomically.
            args can be positional values for ? placeholders or an object of named values for :name, @name or $name.
            Values: null, numbers, booleans (stored as 1 or 0), strings or {base64: "<encoded bytes>"} blobs; returned blobs use the same shape.
            Use parameters for values. Calls outside sql.transaction commit independently.
            Await every SQL call. A refused statement throws SqlError with name="SqlError", code, message, statement (zero-based index in that call's batch) and sqlstate.
            Example sandbox refusal: code="sql_sandbox", sqlstate="42501" for ATTACH.
            Let errors escape for rollback; catching an error inside a transaction and returning normally commits the callback's successful work.

            Console: console.log(...values), console.info(...values), console.warn(...values), console.error(...values).
            Non-string values are JSON-stringified; arguments are joined by spaces. Logs capture output and runtime errors.

            Return rules:
            A returned object with an integer status property from 200 to 599 is ALWAYS a response wrapper; only status and body are used.
            Example: return {status: 200, items: rows} produces an empty response; items is discarded.
            To return data containing a status field, wrap it explicitly: return {status: 200, body: {id: 1, status: 404}}.
            Values without a top-level status property return status 200 application/json; undefined becomes null.
            Treat top-level status as reserved; wrap data containing a status field inside body.
            To set the HTTP status, return {status: <integer 200..599>, body: <value>}.
            With that wrapper, a string body is text/plain; another non-null value is application/json; missing/null body is an empty JSON response.
            A thrown error or runtime limit returns HTTP 500 function_error and an error log.
            Avoid unserializable values such as BigInt or cyclic objects.

            Fixed limits:
            Name: [a-z][a-z0-9_-]{0,62}; reserved con, prn, aux, nul, com0..com9 and lpt0..lpt9 are refused on dashboard/MCP deployment.
            Source: one file, at most 256 KiB. At most 32 functions per database.
            Invocation: 10 s wall time including SQL, 10,000,000 JavaScript statements, recursion depth 256, 32 MiB allocated.
            Request body and result: at most 1 MiB each.
            SQL: at most 256 binding calls per invocation; SQL request deadline 5 s; normal SQL statement, row and transaction limits apply.
            Logs: at most 50 lines, each truncated to 1024 characters; latest 100 invocation records retained per function.
            list_function_logs returns the latest 25. No function secrets binding exists; do not embed credentials in source.
            get_function_stats(databaseId, name, window) reads aggregate metrics independently of retained logs.
            window accepts 1h, 24h (default) or 7d; invalid windows return a structured validation error.
            Returns window, invocations, errors, p50Ms, p95Ms, p99Ms, maxMs, sqlCalls, rowsRead, rowsWritten and buckets.
            Buckets are oldest first, include empty intervals, and contain at, invocations, errors, p50Ms and p95Ms.
            Like list_function_logs, stats require SQL read and write access on signed-in and project API-key connections.

            Example 1: HTTP JSON API over a table (deploy as notes-api)
            async function handler(event) {
              await sql.execute("CREATE TABLE IF NOT EXISTS notes (id INTEGER PRIMARY KEY, text TEXT NOT NULL)");
              if (event.body && typeof event.body.text === "string") {
                const saved = await sql.execute("INSERT INTO notes (text) VALUES (?)", [event.body.text]);
                console.log("created note", saved.lastInsertRowid);
                return { status: 201, body: { id: saved.lastInsertRowid } };
              }
              const notes = await sql.query("SELECT id, text FROM notes ORDER BY id LIMIT 100");
              return { notes };
            }
            Invoke with body {"text":"hello"} to create; invoke with null to list.

            Example 2: cron cleanup (deploy as cleanup, cron "*/5 * * * *")
            async function handler(event) {
              await sql.execute("CREATE TABLE IF NOT EXISTS expired_items (id INTEGER PRIMARY KEY, expires_at TEXT NOT NULL)");
              const cutoff = event.trigger === "cron" ? event.scheduledAt : new Date().toISOString();
              const result = await sql.execute("DELETE FROM expired_items WHERE expires_at < ?", [cutoff]);
              console.info("removed", result.rowsAffected);
              return { removed: result.rowsAffected };
            }

            Example 3: transaction with error handling (deploy as transfer)
            async function handler(event) {
              await sql.execute("CREATE TABLE IF NOT EXISTS wallets (id INTEGER PRIMARY KEY, balance INTEGER NOT NULL CHECK (balance >= 0))");
              await sql.batch([
                { sql: "INSERT OR IGNORE INTO wallets (id, balance) VALUES (?, ?)", args: [1, 100] },
                { sql: "INSERT OR IGNORE INTO wallets (id, balance) VALUES (?, ?)", args: [2, 0] }
              ]);
              const amount = event.body && event.body.amount;
              if (!Number.isSafeInteger(amount) || amount <= 0) return { status: 400, body: { error: "positive integer amount required" } };
              try {
                const wallets = await sql.transaction(async tx => {
                  await tx.execute("UPDATE wallets SET balance = balance - ? WHERE id = 1", [amount]);
                  await tx.execute("UPDATE wallets SET balance = balance + ? WHERE id = 2", [amount]);
                  return await tx.query("SELECT id, balance FROM wallets ORDER BY id");
                });
                console.log("transfer committed");
                return { wallets };
              } catch (error) {
                console.error(error.name, error.message);
                if (error.name === "SqlError") {
                  return { status: 409, body: { code: error.code, message: error.message, statement: error.statement, sqlstate: error.sqlstate } };
                }
                throw error;
              }
            }
            Invoke with body {"amount":10}. A constraint failure rolls back both updates before the catch returns.
            """;
    }
}
