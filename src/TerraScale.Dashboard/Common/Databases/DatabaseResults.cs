using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.TerraBase.Contracts;
using TerraScale.Dashboard.Features.Data;

namespace TerraScale.Dashboard.Common.Databases
{
    internal static class DatabaseResults
    {
        internal static bool WantsHtml(HttpContext http)
        {
            return http.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType.Value == "text/html") == true;
        }

        internal static bool KnownFailure(Exception error)
        {
            return error is AccountAccessException or TsAuthException or TerraBaseException or DatabaseProviderException;
        }

        internal static int Status(Exception error)
        {
            return error switch { AccountAccessException access => access.StatusCode, TerraBaseException provider => provider.Status, DatabaseWrongRegionException => 409, DbUpdateConcurrencyException => StatusCodes.Status412PreconditionFailed, HttpRequestException { StatusCode: { } status } when (int)status is >= 400 and <= 599 => (int)status, _ => 503 };
        }

        internal static string Message(Exception error)
        {
            return error switch
            {
                AccountAccessException or DatabaseWrongRegionException or DatabaseSqlException or SqlOutcomeUnknownException or DatabaseRegionsCopyingException => error.Message,
                TerraBaseException { ErrorCode: "operation_allowance" } => AllowanceUsed,
                TerraBaseException { ErrorCode: MoveInProgress } => RegionsChanging,
                TerraBaseException { ErrorCode: "name_taken" } => error.Message,
                _ => Generic(error) + Reason(error) + RetryHint(error),
            };
        }

        internal const string MoveInProgress = "move_in_progress";
        internal const string RegionsChanging = "Another change to this database's regions is still running. Try again in a moment.";

        private static int? RetrySeconds(Exception error)
        {
            return (error is TerraBaseException rpc ? rpc.RetryAfter : (error as DatabaseProviderException)?.RetryAfter) is { } wait ? (int)Math.Ceiling(Math.Clamp(wait.TotalSeconds, 0, 86400)) : null;
        }

        private static string RetryHint(Exception error)
        {
            return RetrySeconds(error) is { } seconds ? $" Retry after {seconds} s." : "";
        }

        internal const string AllowanceUsed = "This organization reached its plan's operation units for this month, so new writes are paused. " +
            "Reads keep working, and writes resume next month (UTC). Upgrade the plan on the organization's Billing tab to keep writing now; Usage shows this month's total.";
        private static string Generic(Exception error)
        {
            return Status(error) switch { 401 => "The data plane requires valid authentication.", 404 => "The requested resource is unavailable.", 403 => "Organization access is required.", 409 => "The request conflicts with the current database state.", 413 => "The payload is too large. Reduce the size and try again.", 429 => "Too many requests on this database. Wait a moment and try again.", 400 or 422 => "TerraBase refused the request.", _ when AnsweredFailure(error) => "TerraBase could not complete the request.", _ => "The data plane could not complete the request. Try again shortly." };
        }

        private static string Reason(Exception error)
        {
            return error is TerraBaseException { Message: { Length: > 0 and <= 300 } message } && (Status(error) is 400 or 409 or 413 or 422 or 429 || AnsweredFailure(error)) &&
            message.Trim() is { Length: > 0 } reason
            ? " " + char.ToUpperInvariant(reason[0]) + reason[1..] + (reason[^1] is '.' or '!' or '?' ? "" : ".") : "";
        }

        private static bool AnsweredFailure(Exception error)
        {
            return error is TerraBaseException { Status: >= 500, InnerException: RpcException { Trailers: var trailers } } && trailers.GetValueBytes(TerraBaseRpc.FaultTrailer) is not null;
        }

        internal static IResult Failure(Exception error)
        {
            return Results.Problem(Message(error), statusCode: Status(error), extensions: error switch
            {
                SqlOutcomeUnknownException unknown => new Dictionary<string, object?> { ["operationId"] = unknown.AttemptId, ["outcome"] = "unknown" },
                DatabaseRegionsCopyingException => new Dictionary<string, object?> { ["code"] = DatabaseRegionsCopyingException.Code },
                DatabaseSqlException { Code: { } code } sql => new Dictionary<string, object?> { ["code"] = code, ["sqlstate"] = sql.Sqlstate },
                _ when RetrySeconds(error) is { } seconds => new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds },
                TerraBaseException { ErrorCode: { Length: > 0 } code } => new Dictionary<string, object?> { ["code"] = code },
                _ => null
            });
        }

        internal static IResult PageFailure(HttpContext http, AccountContext context, Exception error)
        {
            return WantsHtml(http) && Status(error) == 404 ? Features.NotFound.NotFoundEndpoints.Page(http, context) : Failure(error);
        }
    }
}
