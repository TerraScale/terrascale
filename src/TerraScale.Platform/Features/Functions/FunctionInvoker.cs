using System.Diagnostics;
using System.Text;

namespace TerraScale.Platform.Features.Functions;

public readonly union FunctionInvokeOutcome(FunctionAnswer, PlatformNotFound, PlatformUnavailable);

public sealed class FunctionInvoker(
    FunctionService functions,
    FunctionSecretSeal seal,
    TerraBaseClient terraBase,
    PlatformMetrics metrics,
    TimeProvider timeProvider,
    ILogger<FunctionInvoker> logger) : IDisposable
{
    private const int MaxConcurrent = 64;
    private static readonly PlatformUnavailable Busy = new("function capacity is temporarily unavailable", "function_busy");
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    public async ValueTask<FunctionInvokeOutcome> InvokeAsync(
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
            var outcome = await functions.LoadAsync(target, name, cancellationToken);
            return outcome switch
            {
                FunctionCode code => await RunAsync(code),
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
        finally
        {
            _slots.Release();
        }

        async ValueTask<FunctionInvokeOutcome> RunAsync(FunctionCode code)
        {
            var secrets = OpenSecrets(target, name, code.Secrets);
            return secrets switch
            {
                string opened => await ExecuteOpenedAsync(code.Source, opened),
                PlatformUnavailable unavailable => unavailable,
            };
        }

        async ValueTask<FunctionInvokeOutcome> ExecuteOpenedAsync(string source, string secrets)
        {
            var outcome = await ExecuteAsync(target, name, trigger, eventJson, secrets, bookmark, requestId, source, cancellationToken);
            return outcome switch
            {
                FunctionAnswer answer => answer,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    public void Dispose() => _slots.Dispose();

    private readonly union ExecuteFunctionOutcome(FunctionAnswer, PlatformUnavailable);

    private async ValueTask<ExecuteFunctionOutcome> ExecuteAsync(
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
        await using var sql = new FunctionSql(terraBase, target.Database, bookmark, timeProvider, logger);
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
        var outcome = await functions.RecordAsync(target, name, log, CancellationToken.None);
        return outcome switch
        {
            FunctionRunRecorded => answer,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private readonly union OpenFunctionSecretsOutcome(string, PlatformUnavailable);

    private OpenFunctionSecretsOutcome OpenSecrets(FunctionTarget target, string name, IReadOnlyList<SealedSecret> stored)
    {
        var values = new Dictionary<string, string>(stored.Count, StringComparer.Ordinal);
        foreach (var secret in stored)
        {
            PlatformUnavailable? failure = seal.Open(target.Database, name, secret.Name, secret.Sealed) switch
            {
                string value => Keep(values, secret.Name, value),
                PlatformUnavailable unavailable => unavailable,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return JsonSerializer.Serialize<IReadOnlyDictionary<string, string>>(values, FunctionsJsonContext.Default.IReadOnlyDictionaryStringString);
    }

    private static PlatformUnavailable? Keep(Dictionary<string, string> values, string name, string value)
    {
        values[name] = value;
        return null;
    }
}

public static class FunctionAnswers
{
    public static FunctionAnswer FromResult(string json, string bookmark)
    {
        if (ParseJson(json) is not { } parsed)
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

    public static JsonDocument? ParseJson(string json)
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

    public static JsonDocument? ParseJson(ReadOnlyMemory<byte> json)
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
