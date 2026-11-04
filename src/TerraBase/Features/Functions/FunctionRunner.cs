using Jint;
using Jint.Runtime;
using JsEngine = Jint.Engine;

namespace TerraBase.Features.Functions;

internal sealed record FunctionRun(string? Result, string? Error, List<string> Lines);

internal static class FunctionRunner
{
    public const int MaxSourceChars = 256 * 1024;
    public const long MemoryBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromSeconds(2);
    private const int MaxLines = 50;
    private const int MaxLineChars = 1024;
    private const int MaxResultChars = 1024 * 1024;

    private const string Prelude = """
        "use strict";
        const __text = v => typeof v === "string" ? v : JSON.stringify(v);
        const console = Object.freeze({
            log: (...a) => __log(a.map(__text).join(" ")),
            info: (...a) => __log(a.map(__text).join(" ")),
            warn: (...a) => __log(a.map(__text).join(" ")),
            error: (...a) => __log(a.map(__text).join(" ")),
        });
        const __sqlCall = async (op, statements) => {
            const answer = JSON.parse(await __sql(op, statements === undefined ? "" : JSON.stringify({ statements })));
            if (answer.error) {
                const error = new Error(answer.error.message);
                error.name = "SqlError";
                error.code = answer.error.code;
                error.statement = answer.error.statement;
                error.sqlstate = answer.error.sqlstate;
                throw error;
            }
            return answer.results;
        };
        const __statement = (text, args) => typeof text === "string"
            ? (Array.isArray(args) || args === undefined || args === null ? { sql: text, args: args || [] } : { sql: text, named: args })
            : { sql: text.sql, args: text.args, named: text.named };
        const __result = r => ({ columns: r.columns.map(c => c.name), rows: r.rows, rowsAffected: r.rows_affected, lastInsertRowid: r.last_insert_rowid });
        const __objects = r => r.rows.map(row => Object.fromEntries(r.columns.map((c, i) => [c.name, row[i]])));
        const __binding = Object.freeze({
            execute: async (text, args) => __result((await __sqlCall("execute", [__statement(text, args)]))[0]),
            query: async (text, args) => __objects((await __sqlCall("execute", [__statement(text, args)]))[0]),
            batch: async statements => (await __sqlCall("execute", Array.from(statements, s => __statement(s)))).map(__result),
        });
        const sql = Object.freeze({
            ...__binding,
            transaction: async work => {
                await __sqlCall("begin");
                let value;
                try {
                    value = await work(__binding);
                } catch (error) {
                    try { await __sqlCall("rollback"); } catch (ignored) { }
                    throw error;
                }
                await __sqlCall("commit");
                return value;
            },
        });
        function __run(e) {
            if (typeof handler !== "function") throw new Error("the source must define function handler(event)");
            return Promise.resolve(handler(JSON.parse(e))).then(r => JSON.stringify(r === undefined ? null : r));
        }
        """;

    public static string? Check(string source)
    {
        try
        {
            _ = JsEngine.PrepareScript(source);
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return error.Message;
        }
    }

    public static async Task<FunctionRun> RunAsync(string source, string eventJson, FunctionSql sql, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaxRunTime);
        using var engine = new JsEngine(options =>
        {
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            _ = options.Strict()
                .TimeoutInterval(MaxRunTime)
                .MaxStatements(10_000_000)
                .LimitMemory(MemoryBytes)
                .LimitRecursion(256)
                .CancellationToken(deadline.Token);
        });
        engine.SetValue("__log", new Action<string>(line =>
        {
            if (lines.Count < MaxLines)
            {
                lines.Add(line.Length > MaxLineChars ? line[..MaxLineChars] : line);
            }
        }));
        engine.SetValue("__sql", new Func<string, string, Task<string>>((op, json) => sql.CallAsync(op, json, deadline.Token)));
        try
        {
            engine.Execute(Prelude);
            engine.Execute(source);
            var result = (await engine.Invoke("__run", eventJson).UnwrapIfPromiseAsync(deadline.Token).ConfigureAwait(false)).AsString();
            return result.Length > MaxResultChars
                ? new FunctionRun(null, "the result exceeds 1 MiB", lines)
                : new FunctionRun(result, null, lines);
        }
        catch (JavaScriptException error)
        {
            return new FunctionRun(null, error.Message, lines);
        }
        catch (Exception error) when (error is TimeoutException or MemoryLimitExceededException or StatementsCountOverflowException
            or RecursionDepthOverflowException or ExecutionCanceledException or OperationCanceledException or PromiseRejectedException)
        {
            return new FunctionRun(null, error switch
            {
                TimeoutException => "the function exceeded 2 s",
                MemoryLimitExceededException => "the function exceeded 32 MiB",
                StatementsCountOverflowException => "the function exceeded 10,000,000 statements",
                RecursionDepthOverflowException => "the function exceeded recursion depth 256",
                ExecutionCanceledException or OperationCanceledException => cancellationToken.IsCancellationRequested
                    ? "the invocation was cancelled" : "the function exceeded 2 s",
                _ => error.Message,
            }, lines);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new FunctionRun(null, error.Message, lines);
        }
        finally
        {
            await sql.DrainAsync().ConfigureAwait(false);
        }
    }
}
