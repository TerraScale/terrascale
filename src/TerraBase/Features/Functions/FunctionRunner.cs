using System.Text;
using System.Text.Json;
using Jint;
using Jint.Runtime;
using TerraBase.Engine;
using TerraBase.Features.Documents;
using TerraBase.Primitives;
using JsEngine = Jint.Engine;

namespace TerraBase.Features.Functions;

/// <summary>A finished run: its JSON result or error, console lines and the document writes it recorded.</summary>
internal sealed record FunctionRun(string? Result, string? Error, List<string> Lines, List<DocumentOperation> Writes);
/// <summary>Runs one invocation in a fresh Jint engine without CLR, file, network or environment access.</summary>
internal static class FunctionRunner
{
    public const int MaxSourceChars = 256 * 1024;
    public const long MemoryBytes = 32 * 1024 * 1024;
    private const int MaxLines = 50;
    private const int MaxLineChars = 1024;
    private const int MaxWrites = 25;
    private const int MaxDocumentBytes = 256 * 1024;
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
        const db = Object.freeze({
            insert: (collection, doc) => __write("put", String(collection), null, JSON.stringify(doc)),
            put: (collection, id, doc) => __write("put", String(collection), String(id), JSON.stringify(doc)),
            delete: (collection, id) => __write("delete", String(collection), String(id), null),
        });
        function __run(e) {
            if (typeof handler !== "function") throw new Error("the source must define function handler(event)");
            return Promise.resolve(handler(JSON.parse(e))).then(r => JSON.stringify(r === undefined ? null : r));
        }
        """;

    /// <summary>Null when <paramref name="source"/> parses; otherwise the parser's message.</summary>
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

    public static FunctionRun Run(string source, string eventJson, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var writes = new List<DocumentOperation>();
        using var engine = new JsEngine(options => options
            .Strict()
            .TimeoutInterval(TimeSpan.FromSeconds(2))
            .MaxStatements(10_000_000)
            .LimitMemory(MemoryBytes)
            .LimitRecursion(256)
            .CancellationToken(cancellationToken));
        engine.SetValue("__log", new Action<string>(line =>
        {
            if (lines.Count < MaxLines)
            {
                lines.Add(line.Length > MaxLineChars ? line[..MaxLineChars] : line);
            }
        }));
        engine.SetValue("__write", new Func<string, string, string?, string?, string>((kind, collection, id, json) =>
            Record(writes, kind, collection, id, json)));
        try
        {
            engine.Execute(Prelude);
            engine.Execute(source);
            var result = engine.Invoke("__run", eventJson).UnwrapIfPromise(cancellationToken).AsString();
            return result.Length > MaxResultChars
                ? new FunctionRun(null, "the result exceeds 1 MiB", lines, [])
                : new FunctionRun(result, null, lines, writes);
        }
        catch (JavaScriptException error)
        {
            return new FunctionRun(null, error.Message, lines, []);
        }
        catch (Exception error) when (error is TimeoutException or MemoryLimitExceededException or StatementsCountOverflowException
            or RecursionDepthOverflowException or ExecutionCanceledException or PromiseRejectedException or JavaScriptLimitException)
        {
            return new FunctionRun(null, error switch
            {
                TimeoutException => "the function exceeded 2 s",
                MemoryLimitExceededException => "the function exceeded 32 MiB",
                StatementsCountOverflowException => "the function exceeded 10,000,000 statements",
                RecursionDepthOverflowException => "the function exceeded recursion depth 256",
                ExecutionCanceledException => "the invocation was cancelled",
                _ => error.Message,
            }, lines, []);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new FunctionRun(null, error.Message, lines, []);
        }
    }

    private static string Record(List<DocumentOperation> writes, string kind, string collection, string? id, string? json)
    {
        if (writes.Count >= MaxWrites)
        {
            throw new JavaScriptLimitException($"at most {MaxWrites} writes per invocation");
        }

        if (!DatabaseName.TryParse(collection, out _))
        {
            throw new JavaScriptLimitException("collection must be a lowercase name of at most 63 characters");
        }

        id ??= EntityIds.New().ToString();
        if (!EntityIds.IsValid(id))
        {
            throw new JavaScriptLimitException("id must be a canonical uppercase ULID");
        }

        if (kind == "delete")
        {
            writes.Add(new DeleteOperation(collection, id, new Precondition(null, IfMatchAny: false, IfNoneMatch: false)));
            return id;
        }

        var bytes = Encoding.UTF8.GetBytes(json ?? "null");
        if (bytes.Length > MaxDocumentBytes)
        {
            throw new JavaScriptLimitException("a document is at most 256 KiB");
        }

        try
        {
            using var parsed = JsonDocument.Parse(bytes);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JavaScriptLimitException("a document must be a JSON object");
            }

            DocumentJson.ValidateValue(parsed.RootElement);
        }
        catch (Exception error) when (error is JsonException or DocumentValidationException)
        {
            throw new JavaScriptLimitException("the document is not valid: " + error.Message);
        }

        writes.Add(new PutOperation(collection, id, bytes, new Precondition(null, IfMatchAny: false, IfNoneMatch: false)));
        return id;
    }
}

/// <summary>A host-side refusal surfaced to the function as its error.</summary>
internal sealed class JavaScriptLimitException(string message) : Exception(message);
