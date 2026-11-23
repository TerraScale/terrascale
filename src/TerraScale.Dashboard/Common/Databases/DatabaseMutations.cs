using System.Diagnostics;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed record RegionsCopying(IReadOnlyList<string> Added, IReadOnlyList<string> Copying, IReadOnlyList<string> Serving)
    {
        public const string Code = "replica_not_ready";

        public string Message =>
            (Added.Count > 0 ? $"{RegionNames.List(Added)} added. {(Added.Count == 1 ? "It is" : "They are")}" : $"{RegionNames.List(Copying)} {(Copying.Count == 1 ? "is" : "are")}") +
            $" still copying this database, which usually takes about half a minute. Save again to finish; until then {RegionNames.List(Serving)} {(Serving.Count == 1 ? "keeps" : "keep")} serving.";
    }

    public closed record DatabaseRefusal(string Message)
    {
        public abstract int Status { get; }
    }

    public sealed record DatabaseNotFound(string Message) : DatabaseRefusal(Message)
    {
        public override int Status => StatusCodes.Status404NotFound;
    }

    public sealed record DatabaseStateConflict(string Message) : DatabaseRefusal(Message)
    {
        public override int Status => StatusCodes.Status409Conflict;
    }

    public sealed record DatabaseInvalid(string Message) : DatabaseRefusal(Message)
    {
        public override int Status => StatusCodes.Status422UnprocessableEntity;
    }

    public sealed record DatabaseUnauthorized(string Message) : DatabaseRefusal(Message)
    {
        public override int Status => StatusCodes.Status401Unauthorized;
    }

    public sealed record DatabaseForbidden(string Message) : DatabaseRefusal(Message)
    {
        public override int Status => StatusCodes.Status403Forbidden;
    }

    public sealed record DatabaseQuotaExceeded(string Message) : DatabaseRefusal(Message)
    {
        public override int Status => StatusCodes.Status429TooManyRequests;
    }

    public sealed record DatabaseUnavailable(string Message) : DatabaseRefusal(Message)
    {
        public override int Status => StatusCodes.Status503ServiceUnavailable;
    }

    public readonly union DatabaseMutation(DatabaseRecord, DatabaseRefusal);

    public readonly union DatabaseDeletion(Done, DatabaseRefusal);

    public readonly union PlacementResult(DatabaseRecord, RegionsCopying, DatabaseRefusal);

    public static class DatabaseMutations
    {
        public static DatabaseMutation MutationRefused(DatabaseRefusal refusal) => refusal;

        public static DatabaseDeletion DeletionRefused(DatabaseRefusal refusal) => refusal;

        public static PlacementResult PlacementRefused(DatabaseRefusal refusal) => refusal;

        public static bool Mappable(AccountAccessException error)
        {
            return error.StatusCode is 401 or 403 or 404 or 409 or 429 or 503;
        }

        public static DatabaseRefusal ToRefusal(AccountAccessException error)
        {
            return error.StatusCode switch
            {
                401 => new DatabaseUnauthorized(error.Message),
                403 => new DatabaseForbidden(error.Message),
                404 => new DatabaseNotFound(error.Message),
                409 => new DatabaseStateConflict(error.Message),
                429 => new DatabaseQuotaExceeded(error.Message),
                503 => new DatabaseUnavailable(error.Message),
                _ => throw new UnreachableException()
            };
        }

        public static AccountAccessException ToError(DatabaseRefusal refusal)
        {
            return new(refusal.Message, refusal.Status);
        }
    }
}
