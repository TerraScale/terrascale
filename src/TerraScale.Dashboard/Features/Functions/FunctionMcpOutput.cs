using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using TerraScale.TerraBase.Contracts.Functions;

namespace TerraScale.Dashboard.Features.Functions
{
    internal static partial class FunctionMcpOutput
    {
        internal const string LogNote = "Log lines need SQL write access; call list_function_logs with a key that has it.";

        internal static CallToolResult Reply<T>(T value, JsonTypeInfo<T> type, bool error = false) => new()
        {
            IsError = error,
            StructuredContent = JsonSerializer.SerializeToElement(value, type),
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, type) }]
        };

        internal static CallToolResult Deployed(string name, string? cron, DateTimeOffset updatedAt, string url) =>
            Reply(new McpFunctionDeployment(name, cron, updatedAt, url), FunctionMcpJson.Default.McpFunctionDeployment);

        internal static CallToolResult SecretSaved(TerraBaseFunctionSecret secret) =>
            Reply(new McpFunctionSecret(secret.Name, secret.UpdatedAt), FunctionMcpJson.Default.McpFunctionSecret);

        internal static McpFunctionSecrets Secrets(IReadOnlyList<TerraBaseFunctionSecret> secrets) =>
            new([.. secrets.Select(secret => new McpFunctionSecret(secret.Name, secret.UpdatedAt))]);

        internal static CallToolResult Invalid(string code, string message)
        {
            var position = Position().Match(message);
            int? line = int.TryParse(position.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var row) ? row : null;
            int? column = int.TryParse(position.Groups["column"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var col) ? col : null;
            return Reply(new McpFunctionFailure(new(code, message, line, column)), FunctionMcpJson.Default.McpFunctionFailure, true);
        }

        internal static async Task<McpFunctionRun> CompleteAsync(TerraBaseFunctionCall call, bool canReadLogs,
            Func<CancellationToken, Task<IReadOnlyList<FunctionInvocation>>> readLogs, ILogger logger, CancellationToken cancellationToken)
        {
            if (!canReadLogs)
            {
                return Run(call, null, LogNote);
            }

            FunctionInvocation? newest = null;
            var note = "Newest log after the call; concurrent invocations or cron may produce a newer record.";
            try
            {
                var logs = await readLogs(cancellationToken);
                FunctionBackend.Validate(logs);
                newest = logs.Count > 0 ? logs[0] : null;
            }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException or TimeoutException or JsonException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                LogRetrievalFailed(logger, error);
                note = "Function execution completed. Log retrieval failed: " + DatabaseResults.Message(error) +
                    " Do not invoke again to retrieve logs; call list_function_logs to inspect existing records.";
            }

            return Run(call, newest, note);
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Function execution completed but its invocation log could not be read.")]
        private static partial void LogRetrievalFailed(ILogger logger, Exception exception);

        private static McpFunctionRun Run(TerraBaseFunctionCall call, FunctionInvocation? log, string logNote)
        {
            var body = call.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true && call.Body.Length > 0
                ? JsonDocument.Parse(call.Body) : null;
            using (body)
            {
                return new(call.Status, call.ContentType, body?.RootElement.Clone() ?? JsonSerializer.SerializeToElement(call.Body, FunctionMcpJson.Default.String),
                    log is null ? null : new(log.Id, log.Status, log.DurationMs, log.Lines, log.Error),
                    logNote);
            }
        }

        [GeneratedRegex(@"(?:\((?:[^():\r\n]+:)?(?<line>\d+):(?<column>\d+)\)|[Ll]ine\s+(?<line>\d+)(?:\s*[,;:]?\s*[Cc]olumn\s+(?<column>\d+))?)")]
        private static partial Regex Position();
    }

    public sealed record McpFunctionReference(string Reference);
    public sealed record McpFunctionDeployment(string Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Cron,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt, [property: JsonPropertyName("invoke_url")] string InvokeUrl);
    public sealed record McpFunctionError(string Code, string Message, int? Line, int? Column);
    public sealed record McpFunctionFailure(McpFunctionError Error);
    public sealed record McpFunctionLog(string Id, string Status, [property: JsonPropertyName("duration_ms")] double DurationMs, IReadOnlyList<string> Lines,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error);
    public sealed record McpFunctionRun(int Status, [property: JsonPropertyName("content_type")] string? ContentType, JsonElement Body,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] McpFunctionLog? Log, [property: JsonPropertyName("log_note")] string LogNote);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(McpFunctionReference))]
    [JsonSerializable(typeof(McpFunctionDeployment))]
    [JsonSerializable(typeof(McpFunctionFailure))]
    [JsonSerializable(typeof(McpFunctionRun))]
    [JsonSerializable(typeof(McpFunctionList))]
    [JsonSerializable(typeof(McpFunctionDetail))]
    [JsonSerializable(typeof(McpFunctionLogs))]
    [JsonSerializable(typeof(TerraBaseFunctionStats))]
    [JsonSerializable(typeof(McpFunctionSecret))]
    [JsonSerializable(typeof(McpFunctionSecrets))]
    [JsonSerializable(typeof(McpFunctionSecretDeleted))]
    [JsonSerializable(typeof(string))]
    internal sealed partial class FunctionMcpJson : JsonSerializerContext;
}
