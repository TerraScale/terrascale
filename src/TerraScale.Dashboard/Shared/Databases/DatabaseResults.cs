using TerraScale.DotNet.TerraBase;
using TerraScale.Dashboard.Features.Data;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Shared.Databases;

internal static class DatabaseResults
{
    internal static bool WantsHtml(HttpContext http) => http.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType.Value == "text/html") == true;
    internal static bool KnownFailure(Exception error) => error is AccountAccessException or TerraScaleAuthException or TerraBaseValidationException or TerraBaseAuthenticationException or TerraBaseForbiddenException or TerraBaseNotFoundException or TerraBaseConflictException or TerraBasePreconditionFailedException or TerraBaseUnavailableException or DatabaseProviderException or TerraBaseException { StatusCode: System.Net.HttpStatusCode.RequestEntityTooLarge or System.Net.HttpStatusCode.TooManyRequests };
    internal static int Status(Exception error) => error switch { AccountAccessException access => access.StatusCode, DatabaseWrongRegionException => 409, HttpRequestException { StatusCode: { } status } when (int)status is >= 400 and <= 599 => (int)status, _ => 503 };
    internal static string Message(Exception error) => error is AccountAccessException or DatabaseWrongRegionException or DatabaseSqlException or DocumentWriteOutcomeUnknownException ? error.Message
        : error is TerraBaseException { ErrorCode: "operation_allowance" } ? AllowanceUsed : Generic(error) + Reason(error);
    // A plan allowance refusal lasts until the month ends or the plan changes; retrying soon never helps.
    internal const string AllowanceUsed = "This organization used its plan's operation units for this month, so new writes are paused. " +
        "Reads and deletes keep working, and writes resume next month (UTC). Upgrade the plan on the organization's Billing tab to keep writing now; Usage shows this month's total.";
    private static string Generic(Exception error) => Status(error) switch { 401 => "The data plane requires valid authentication.", 404 => "The requested resource is unavailable.", 403 => "Organization access is required.", 409 => "The request conflicts with the current database state.", 413 => "The payload is too large. Reduce the size and try again.", 429 => "Too many requests on this database. Wait a moment and try again.", 412 => "The document changed. Reload it before saving again.", 400 or 422 => "TerraBase refused the request.", _ => "The data plane could not complete the request. Try again shortly." };
    // TerraBase error messages name the rule that failed and never echo request input, so a refusal can say why.
    // TerraBase writes lowercase fragments ("the cursor does not belong to this query"); present them as a sentence.
    private static string Reason(Exception error) => error is TerraBaseException { Message: { Length: > 0 and <= 300 } message } && Status(error) is 400 or 409 or 413 or 422 or 429 &&
        message.Trim() is { Length: > 0 } reason
        ? " " + char.ToUpperInvariant(reason[0]) + reason[1..] + (reason[^1] is '.' or '!' or '?' ? "" : ".") : "";
    internal static IResult Failure(Exception error) => Results.Problem(Message(error), statusCode: Status(error),
        extensions: error is DocumentWriteOutcomeUnknownException unknown ? new Dictionary<string, object?>
        {
            ["operationId"] = unknown.OperationId,
            ["documentId"] = unknown.DocumentId,
            ["outcome"] = "unknown"
        } : null);
    // Browsers get the HTML not-found page for a database they cannot see; API clients keep problem+json.
    internal static IResult PageFailure(HttpContext http, AccountContext context, Exception error) =>
        WantsHtml(http) && Status(error) == 404 ? Features.NotFound.NotFoundEndpoints.Page(http, context) : Failure(error);
}
