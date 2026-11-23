using System.Diagnostics;
using System.Text;

namespace TerraScale.Platform.Features.Functions;

public sealed class FunctionInvoker(
    FunctionService functions,
    FunctionSecretSeal seal,
    TerraBaseClient terraBase,
    PlatformMetrics metrics,
    TimeProvider timeProvider) : IDisposable
{
    private const int MaxConcurrent = 64;
    private static readonly Unavailable Busy = new("function capacity is temporarily unavailable", "function_busy");
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    public async ValueTask<Result<FunctionAnswer, NotFound, Unavailable>> InvokeAsync(
        FunctionTarget target,
        string name,
        string trigger,
        string eventJson,
        string bookmark,
        string? requestId,
        CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return Busy;
        }

        try
        {
            return await functions.LoadAsync(target, name, cancellationToken) switch
            {
                FunctionCode code => await RunAsync(target, name, trigger, eventJson, bookmark, requestId, code, cancellationToken),
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            };
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();

    private async ValueTask<Result<FunctionAnswer, NotFound, Unavailable>> RunAsync(
        FunctionTarget target,
        string name,
        string trigger,
        string eventJson,
        string bookmark,
        string? requestId,
        FunctionCode code,
        CancellationToken cancellationToken) =>
        OpenSecrets(target, name, code.Secrets) switch
        {
            string secrets => await ExecuteAsync(target, name, trigger, eventJson, secrets, bookmark, requestId, code.Source, cancellationToken),
            Unavailable unavailable => unavailable,
        };

    private async ValueTask<Result<FunctionAnswer, NotFound, Unavailable>> ExecuteAsync(
        FunctionTarget target,
        string name,
        string trigger,
        string eventJson,
        string secrets,
        string bookmark,
        string? requestId,
        string source,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var clock = timeProvider.GetTimestamp();
        await using var sql = new FunctionSql(terraBase, target.Database, bookmark, timeProvider);
        var run = await FunctionRunner.RunAsync(source, eventJson, secrets, sql, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = timeProvider.GetElapsedTime(clock);
        var (answer, lines, error) = run switch
        {
            FunctionReturned returned => (FunctionAnswers.FromResult(returned.Json, sql.Bookmark), returned.Lines, (string?)null),
            FunctionFailed failed => (FunctionAnswers.Failed(failed.Error, requestId, sql.Bookmark), failed.Lines, failed.Error),
        };
        var log = new FunctionLogBody(
            Ulid.NewUlid(started).ToString(),
            started,
            trigger,
            error is null ? "ok" : "error",
            (long)Math.Ceiling(elapsed.TotalMilliseconds),
            lines,
            error,
            sql.SqlCalls,
            sql.SqlMs,
            sql.RowsRead,
            sql.RowsWritten,
            answer.Body.LongLength,
            trigger == "http" && error is null ? answer.Status : null);
        metrics.Invocation(trigger, error is null, elapsed.TotalMilliseconds);
        return await functions.RecordAsync(target, name, log, CancellationToken.None) switch
        {
            Done => answer,
            Unavailable unavailable => unavailable,
        };
    }

    private Result<string, Unavailable> OpenSecrets(FunctionTarget target, string name, IReadOnlyList<SealedSecret> stored)
    {
        var values = new Dictionary<string, string>(stored.Count, StringComparer.Ordinal);
        foreach (var secret in stored)
        {
            Unavailable? failure = seal.Open(target.Database, name, secret.Name, secret.Sealed) switch
            {
                string value => Keep(values, secret.Name, value),
                Unavailable unavailable => unavailable,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return JsonSerializer.Serialize<IReadOnlyDictionary<string, string>>(values, FunctionsJsonContext.Default.IReadOnlyDictionaryStringString);
    }

    private static Unavailable? Keep(Dictionary<string, string> values, string name, string value)
    {
        values[name] = value;
        return null;
    }
}

public static class FunctionAnswers
{
    public static FunctionAnswer FromResult(string json, string bookmark)
    {
        if (Document(json) is not { } parsed)
        {
            return new FunctionAnswer(StatusCodes.Status200OK, "application/json", Encoding.UTF8.GetBytes(json), bookmark);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code is >= 200 and <= 599)
            {
                return !root.TryGetProperty("body", out var body) || body.ValueKind == JsonValueKind.Null
                    ? new FunctionAnswer(code, "application/json", [], bookmark)
                    : body.ValueKind == JsonValueKind.String
                        ? new FunctionAnswer(code, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(body.GetString() ?? throw new UnreachableException()), bookmark)
                        : new FunctionAnswer(code, "application/json", Encoding.UTF8.GetBytes(body.GetRawText()), bookmark);
            }

            return new FunctionAnswer(StatusCodes.Status200OK, "application/json", Encoding.UTF8.GetBytes(json), bookmark);
        }
    }

    public static FunctionAnswer Failed(string error, string? requestId, string bookmark) =>
        new(
            StatusCodes.Status500InternalServerError,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new FunctionErrorBody(new FunctionErrorDetail("function_error", error, requestId)), FunctionsJsonContext.Default.FunctionErrorBody),
            bookmark);

    public static JsonDocument? Document(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonDocument? Document(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
