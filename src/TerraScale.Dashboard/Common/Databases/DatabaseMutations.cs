using System.Diagnostics;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Databases;
using ValidationCase = TerraScale.Dashboard.Errors.Validation;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed record RegionsCopying(IReadOnlyList<string> Added, IReadOnlyList<string> Copying, IReadOnlyList<string> Serving)
    {
        public const string Code = "replica_not_ready";

        public string Message =>
            (Added.Count > 0 ? $"{RegionNames.List(Added)} added. {(Added.Count == 1 ? "It is" : "They are")}" : $"{RegionNames.List(Copying)} {(Copying.Count == 1 ? "is" : "are")}") +
            $" still copying this database, which usually takes about half a minute. Save again to finish; until then {RegionNames.List(Serving)} {(Serving.Count == 1 ? "keeps" : "keep")} serving.";
    }

    public readonly union DatabaseMutation(DatabaseRecord, Failure);

    public readonly union DatabaseDeletion(Done, Failure);

    public readonly union PlacementResult(DatabaseRecord, RegionsCopying, Failure);

    public static class DatabaseMutations
    {
        public static DatabaseMutation MutationRefused(Failure failure) => failure;

        public static DatabaseDeletion DeletionRefused(Failure failure) => failure;

        public static PlacementResult PlacementRefused(Failure failure) => failure;

        public static Failure RefusedConflict(ConflictListing listing)
        {
            return listing switch
            {
                ValidationCase failure => failure,
                DatabaseConflictList => throw new UnreachableException()
            };
        }

        public static Failure RefusedMutation(DatabaseMutation mutation)
        {
            return mutation switch
            {
                Failure failure => failure,
                DatabaseRecord => throw new UnreachableException()
            };
        }

        public static Failure RefusedDeletion(DatabaseDeletion deletion)
        {
            return deletion switch
            {
                Failure failure => failure,
                Done => throw new UnreachableException()
            };
        }

        public static Failure RefusedPlacement(PlacementResult placement)
        {
            return placement switch
            {
                Failure failure => failure,
                RegionsCopying => throw new UnreachableException(),
                DatabaseRecord => throw new UnreachableException()
            };
        }

        public static bool Mappable(AccountAccessException error)
        {
            return error.StatusCode is 401 or 403 or 404 or 409 or 429 or 503;
        }

        public static Failure ToFailure(AccountAccessException error)
        {
            return error.StatusCode switch
            {
                401 => new Unauthorized(error.Message),
                403 => new Forbidden(error.Message),
                404 => new NotFound(error.Message),
                409 => new Conflict(error.Message),
                429 => new QuotaExceeded(error.Message),
                503 => new Unavailable(error.Message),
                _ => throw new UnreachableException()
            };
        }

        public static AccountAccessException ToError(Failure failure)
        {
            return new(failure switch
            {
                NotFound error => error.Message ?? "Not found.",
                Conflict error => error.Message,
                PreconditionFailed error => error.Message,
                Unauthorized error => error.Message ?? "Unauthorized.",
                Forbidden error => error.Message,
                QuotaExceeded error => error.Message,
                Unavailable error => error.Message,
                PayloadTooLarge error => error.Message,
                UnsupportedMediaType error => error.Message,
                RequestTimeout error => error.Message,
                Gone error => error.Message,
                OutcomeUnknown error => error.Message,
                ValidationCase error => error.Message,
            }, StatusOf(failure));
        }

        public static int StatusOf(Failure failure)
        {
            return failure switch
            {
                NotFound => StatusCodes.Status404NotFound,
                Conflict => StatusCodes.Status409Conflict,
                PreconditionFailed => StatusCodes.Status412PreconditionFailed,
                Unauthorized => StatusCodes.Status401Unauthorized,
                Forbidden => StatusCodes.Status403Forbidden,
                QuotaExceeded => StatusCodes.Status429TooManyRequests,
                Unavailable => StatusCodes.Status503ServiceUnavailable,
                PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
                UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
                RequestTimeout => StatusCodes.Status408RequestTimeout,
                Gone => StatusCodes.Status410Gone,
                OutcomeUnknown => StatusCodes.Status503ServiceUnavailable,
                ValidationCase => StatusCodes.Status422UnprocessableEntity,
            };
        }

        public static string MessageOf(Failure failure)
        {
            return failure switch
            {
                NotFound error => error.Message ?? "Not found.",
                Conflict error => error.Message,
                PreconditionFailed error => error.Message,
                Unauthorized error => error.Message ?? "Unauthorized.",
                Forbidden error => error.Message,
                QuotaExceeded error => error.Message,
                Unavailable error => error.Message,
                PayloadTooLarge error => error.Message,
                UnsupportedMediaType error => error.Message,
                RequestTimeout error => error.Message,
                Gone error => error.Message,
                OutcomeUnknown error => error.Message,
                ValidationCase error => error.Message,
            };
        }
    }
}
