using TerraBase.Features.Sql;
using TerraBase.Hosting;

namespace TerraBase.Features.Rows;

public sealed record RowsErrorDetails(string? Table = null, string? Column = null, int? Index = null, int? Clause = null);

public sealed record RowsErrorBody(string Code, string Message, RowsErrorDetails? Details);

public sealed record RowsErrorEnvelope(RowsErrorBody Error);

public sealed record RowRefused(int Status, string Code, string Message, RowsErrorDetails? Details = null) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = Status;
        httpContext.Response.Headers.CacheControl = "no-store";
        return httpContext.Response.WriteAsJsonAsync(new RowsErrorEnvelope(new RowsErrorBody(Code, Message, Details)), RowsJsonContext.Default.RowsErrorEnvelope);
    }
}

public readonly union RowsOutcome(IReadOnlyList<RowAnswer>, RowRefused, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

public readonly union RowResponse(RowAnswer, RowRefused, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

public readonly union CreatedRowResponse(Created<RowAnswer>, RowRefused, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

public readonly union DeletedRowResponse(Completed, RowRefused, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

public readonly union RowsBatchResponse(RowsBatchAnswer, RowRefused, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal static class RowsResponses
{
    private static readonly ResourceNotFound RowNotFound = new("the row does not exist", "row_not_found");

    public static RowResponse Single(RowsOutcome outcome) => outcome switch
    {
        IReadOnlyList<RowAnswer> { Count: > 0 } answers => answers[0],
        IReadOnlyList<RowAnswer> => RowNotFound,
        RowRefused refused => refused,
        SqlRefusal refusal => refusal,
        InvalidInput invalid => invalid,
        CredentialRejected unauthorized => unauthorized,
        CallerForbidden forbidden => forbidden,
        ResourceNotFound missing => missing,
        BackendUnavailable unavailable => unavailable,
    };

    public static CreatedRowResponse Created(RowsOutcome outcome, string rows) => outcome switch
    {
        IReadOnlyList<RowAnswer> { Count: > 0 } answers => new Created<RowAnswer>(string.Concat(rows, "/", answers[0].Key), answers[0]),
        IReadOnlyList<RowAnswer> => new RowRefused(StatusCodes.Status403Forbidden, "rule_denied", "the rules refused the row"),
        RowRefused refused => refused,
        SqlRefusal refusal => refusal,
        InvalidInput invalid => invalid,
        CredentialRejected unauthorized => unauthorized,
        CallerForbidden forbidden => forbidden,
        ResourceNotFound missing => missing,
        BackendUnavailable unavailable => unavailable,
    };

    public static DeletedRowResponse Deleted(RowsOutcome outcome) => outcome switch
    {
        IReadOnlyList<RowAnswer> { Count: > 0 } => new Completed(),
        IReadOnlyList<RowAnswer> => RowNotFound,
        RowRefused refused => refused,
        SqlRefusal refusal => refusal,
        InvalidInput invalid => invalid,
        CredentialRejected unauthorized => unauthorized,
        CallerForbidden forbidden => forbidden,
        ResourceNotFound missing => missing,
        BackendUnavailable unavailable => unavailable,
    };

    public static RowsBatchResponse Batch(RowsOutcome outcome) => outcome switch
    {
        IReadOnlyList<RowAnswer> answers => new RowsBatchAnswer(answers),
        RowRefused refused => refused,
        SqlRefusal refusal => refusal,
        InvalidInput invalid => invalid,
        CredentialRejected unauthorized => unauthorized,
        CallerForbidden forbidden => forbidden,
        ResourceNotFound missing => missing,
        BackendUnavailable unavailable => unavailable,
    };
}
