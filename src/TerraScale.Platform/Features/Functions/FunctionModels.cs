using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TerraScale.Platform.Features.Functions;

public sealed record DeployFunctionRequest(string Project, string Database, string Function, string? Source, string? Cron);

public sealed record FunctionDatabaseRequest(string Project, string Database);

public sealed record FunctionRequest(string Project, string Database, string Function);

public sealed record FunctionLogsRequest(string Project, string Database, string Function, string? Limit);

public sealed record FunctionStatsRequest(string Project, string Database, string Function, string? Window);

public sealed record SetFunctionSecretRequest(string Project, string Database, string Function, string Secret, string? Value);

public sealed record FunctionSecretRequest(string Project, string Database, string Function, string Secret);

public sealed record FunctionBody(string Name, string? Source, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record FunctionLogBody(
    string Id,
    DateTimeOffset At,
    string Trigger,
    string Status,
    long DurationMs,
    IReadOnlyList<string> Lines,
    string? Error,
    int SqlCalls = 0,
    long SqlMs = 0,
    long RowsRead = 0,
    long RowsWritten = 0,
    long ResponseBytes = 0,
    int? HttpStatus = null);

public sealed record FunctionSecretBody(string Name, DateTimeOffset UpdatedAt);

public sealed record FunctionStatsBucket(DateTimeOffset At, long Invocations, long Errors, long P50Ms, long P95Ms);

public sealed record FunctionStats(
    string Window,
    long Invocations,
    long Errors,
    long P50Ms,
    long P95Ms,
    long P99Ms,
    long MaxMs,
    long SqlCalls,
    long RowsRead,
    long RowsWritten,
    IReadOnlyList<FunctionStatsBucket> Buckets);

public sealed record FunctionList(IReadOnlyList<FunctionBody> Items);

public sealed record FunctionLogList(IReadOnlyList<FunctionLogBody> Items);

public sealed record FunctionSecretList(IReadOnlyList<FunctionSecretBody> Items);

public sealed record FunctionTarget(Ulid Organization, Ulid Project, Ulid Database, string DatabaseName);

public sealed record FunctionCode(string Source, IReadOnlyList<SealedSecret> Secrets);

public sealed record SealedSecret(string Name, string Sealed);

public sealed record FunctionErrorDetail(string Code, string Message, string? RequestId);

public sealed record FunctionErrorBody(FunctionErrorDetail Error);

public sealed class FunctionAnswer(int status, string contentType, byte[] body, string bookmark) : IResult
{
    public int Status => status;

    public string ContentType => contentType;

    public byte[] Body => body;

    public string Bookmark => bookmark;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var response = httpContext.Response;
        response.Headers["X-TerraScale-Function-Response"] = "1";
        if (bookmark.Length > 0)
        {
            response.Headers[ConsistencyHeaders.Bookmark] = bookmark;
        }

        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, httpContext.RequestAborted);
    }
}

public static partial class FunctionNames
{
    public const string RouteList = "/v1/projects/{project}/databases/{database}/functions";
    public const string RouteItem = RouteList + "/{function}";
    public const string RouteSecrets = RouteItem + "/secrets";
    public const string RouteSecret = RouteSecrets + "/{secret}";
    public const string Description = "Project-token database functions. Deploy, logs, stats, secrets and delete require sql:read and sql:write; "
        + "invoke requires sql:read. A function is JavaScript defining handler(event); it runs with the function's authority against the database.";

    public static PlatformInvalidRequest? Check(string function) =>
        NamePattern().IsMatch(function) ? null : new PlatformInvalidRequest("function must be a lowercase name of at most 63 characters");

    public static PlatformInvalidRequest? CheckSecret(string secret) =>
        SecretPattern().IsMatch(secret) ? null : new PlatformInvalidRequest("secret name must match ^[A-Z_][A-Z0-9_]{0,63}$");

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A[A-Z_][A-Z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DeployFunctionRequest))]
[JsonSerializable(typeof(FunctionDatabaseRequest))]
[JsonSerializable(typeof(FunctionRequest))]
[JsonSerializable(typeof(FunctionLogsRequest))]
[JsonSerializable(typeof(FunctionStatsRequest))]
[JsonSerializable(typeof(SetFunctionSecretRequest))]
[JsonSerializable(typeof(FunctionSecretRequest))]
[JsonSerializable(typeof(FunctionBody))]
[JsonSerializable(typeof(IReadOnlyList<FunctionBody>))]
[JsonSerializable(typeof(FunctionLogBody))]
[JsonSerializable(typeof(IReadOnlyList<FunctionLogBody>))]
[JsonSerializable(typeof(FunctionSecretBody))]
[JsonSerializable(typeof(IReadOnlyList<FunctionSecretBody>))]
[JsonSerializable(typeof(FunctionStats))]
[JsonSerializable(typeof(FunctionErrorBody))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<SqlHttpStatementResult>))]
public sealed partial class FunctionsJsonContext : JsonSerializerContext;
