using TerraScale.DotNet.TerraBase;
using TerraScale.Dashboard.Features.Data;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Shared.Databases;

internal static class DatabaseResults
{
    internal static bool WantsHtml(HttpContext http) => http.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType.Value == "text/html") == true;
    internal static bool KnownFailure(Exception error) => error is AccountAccessException or TerraScaleAuthException or TerraBaseValidationException or TerraBaseAuthenticationException or TerraBaseForbiddenException or TerraBaseNotFoundException or TerraBaseConflictException or TerraBasePreconditionFailedException or TerraBaseUnavailableException or DatabaseProviderException;
    internal static int Status(Exception error) => error switch { AccountAccessException access => access.StatusCode, DatabaseWrongRegionException => 409, HttpRequestException { StatusCode: { } status } when (int)status is >= 400 and <= 599 => (int)status, _ => 503 };
    internal static string Message(Exception error) => error is AccountAccessException or DatabaseWrongRegionException or DocumentWriteOutcomeUnknownException ? error.Message : Status(error) switch { 401 => "The data plane requires valid authentication.", 404 => "The requested resource is unavailable.", 403 => "Organization access is required.", 409 => "The request conflicts with the current database state.", 412 => "The document changed. Reload it before saving again.", 400 or 422 => "The data plane refused the submitted input.", _ => "The data plane could not complete the request. Try again shortly." };
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
