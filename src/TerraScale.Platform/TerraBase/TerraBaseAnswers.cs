using System.Text.Json.Serialization.Metadata;
using TerraScale.Contracts;

namespace TerraScale.Platform.TerraBase;

public sealed record SqlFailure(int Status, ApiErrorBody Error);

public sealed record SqlContext(string Bookmark, SqlConsistencyHint Hint)
{
    public static SqlContext Fast { get; } = new("", SqlConsistencyHint.Fast);

    public static SqlContext Strong { get; } = new("", SqlConsistencyHint.Strong);

    public static SqlContext After(string bookmark) => new(bookmark, SqlConsistencyHint.DatabaseDefault);
}

public static class TerraBaseAnswers
{
    public static Result<SqlExecuteResponse, SqlFailure, NotFound, Unavailable> Sql(int status, ReadOnlySpan<byte> body)
    {
        if (status == StatusCodes.Status200OK)
        {
            return Parse(body, TerraScaleContractsJsonContext.Default.SqlExecuteResponse) is { } response
                ? response
                : new Unavailable("TerraBase answered an unreadable SQL result", "terrabase_protocol");
        }

        var error = Error(status, body);
        return status switch
        {
            StatusCodes.Status404NotFound => new NotFound(error.Message, error.Code),
            StatusCodes.Status400BadRequest or StatusCodes.Status409Conflict or StatusCodes.Status408RequestTimeout or StatusCodes.Status413PayloadTooLarge
                or StatusCodes.Status422UnprocessableEntity or StatusCodes.Status504GatewayTimeout => new SqlFailure(status, error),
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden =>
                new Unavailable("TerraBase refused the platform peer credential", "peer_credential_refused"),
            _ => new Unavailable(error.Message, error.Code),
        };
    }

    public static ApiErrorBody Error(int status, ReadOnlySpan<byte> body) =>
        Parse(body, TerraScaleContractsJsonContext.Default.ApiErrorEnvelope) is { Error: { } error }
            ? error
            : new ApiErrorBody(status >= 500 ? "unavailable" : "terrabase_error", $"TerraBase answered HTTP {status}", null, null, null);

    public static T? Parse<T>(ReadOnlySpan<byte> body, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (body.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
