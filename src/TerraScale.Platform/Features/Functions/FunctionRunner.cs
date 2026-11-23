using Acornima;
using Jint;
using Jint.Runtime;
using JsEngine = Jint.Engine;

namespace TerraScale.Platform.Features.Functions;

public sealed record FunctionReturned(string Json, IReadOnlyList<string> Lines);

public sealed record FunctionFailed(string Error, IReadOnlyList<string> Lines);

public readonly union FunctionRun(FunctionReturned, FunctionFailed);

public sealed record SourceRejected(string Message, int Line, int Column);

public static class FunctionRunner
{
    public const int MaxSourceChars = 256 * 1024;
    public const long MemoryBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan MaxRunTime = TimeSpan.FromSeconds(10);
    private const int MaxStatements = 10_000_000;
    private const int MaxRecursion = 256;
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
                error.sqlstate = answer.error.sqlState;
                throw error;
            }
            return answer.results;
        };
        const __statement = (text, args) => typeof text === "string"
            ? (Array.isArray(args) || args === undefined || args === null ? { sql: text, args: args || [] } : { sql: text, named: args })
            : { sql: text.sql, args: text.args, named: text.named };
        const __result = r => ({ columns: r.columns.map(c => c.name), rows: r.rows, rowsAffected: r.rowsAffected, lastInsertRowid: r.lastInsertRowId });
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
        const db = sql;
        function __run(e, s) {
            if (typeof handler !== "function") throw new Error("the source must define function handler(event)");
            const event = JSON.parse(e);
            Object.defineProperty(event, "secrets", { value: Object.freeze(JSON.parse(s)), enumerable: false });
            return Promise.resolve(handler(event)).then(r => JSON.stringify(r === undefined ? null : r));
        }
        """;

    public static SourceRejected? Check(string source)
    {
        try
        {
            _ = JsEngine.PrepareScript(source);
            return null;
        }
        catch (ScriptPreparationException error) when (error.InnerException is ParseErrorException parse)
        {
            return new SourceRejected(parse.Message, parse.LineNumber, parse.Column);
        }
        catch (ScriptPreparationException error)
        {
            return new SourceRejected(error.Message, 0, 0);
        }
        catch (ParseErrorException error)
        {
            return new SourceRejected(error.Message, error.LineNumber, error.Column);
        }
    }

    public static async Task<FunctionRun> RunAsync(string source, string eventJson, string secretsJson, FunctionSql sql, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaxRunTime);
        using var engine = new JsEngine(options =>
        {
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            _ = options.Strict()
                .MaxStatements(MaxStatements)
                .LimitMemory(MemoryBytes)
                .LimitRecursion(MaxRecursion)
                .CancellationToken(deadline.Token);
        });
        engine.SetValue("__log", new Action<string>(line =>
        {
            if (lines.Count < MaxLines)
            {
                lines.Add(line.Length > MaxLineChars ? line[..MaxLineChars] : line);
            }
        }));
        engine.SetValue("__sql", new Func<string, string, Task<string>>((operation, json) => sql.CallAsync(operation, json, deadline.Token)));
        try
        {
            engine.Execute(Prelude);
            engine.Execute(source);
            var result = (await engine.Invoke("__run", eventJson, secretsJson).UnwrapIfPromiseAsync(deadline.Token)).AsString();
            return result.Length > MaxResultChars
                ? new FunctionFailed("the result exceeds 1 MiB", lines)
                : new FunctionReturned(result, lines);
        }
        catch (JavaScriptException error)
        {
            return new FunctionFailed(error.Message, lines);
        }
        catch (PromiseRejectedException error)
        {
            return new FunctionFailed(error.Message, lines);
        }
        catch (MemoryLimitExceededException)
        {
            return new FunctionFailed("the function exceeded 32 MiB", lines);
        }
        catch (StatementsCountOverflowException)
        {
            return new FunctionFailed("the function exceeded 10,000,000 statements", lines);
        }
        catch (RecursionDepthOverflowException)
        {
            return new FunctionFailed("the function exceeded recursion depth 256", lines);
        }
        catch (ExecutionCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new FunctionFailed("the function exceeded 10 s", lines);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FunctionFailed("the function exceeded 10 s", lines);
        }
        catch (ParseErrorException error)
        {
            return new FunctionFailed(error.Message, lines);
        }
        catch (ScriptPreparationException error)
        {
            return new FunctionFailed(error.Message, lines);
        }
        catch (JintException error)
        {
            return new FunctionFailed(error.Message, lines);
        }
        finally
        {
            await sql.DrainAsync();
        }
    }
}
