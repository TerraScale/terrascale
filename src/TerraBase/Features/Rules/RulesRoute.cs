using System.Diagnostics;
using System.Globalization;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Rules;

internal sealed record RulesPage(string? After, int Limit);

internal static class RulesRoute
{
    internal const string Rules = ProjectApiRoute.Database + "/rules";
    internal const string Versions = Rules + "/versions";
    internal const string Version = Versions + "/{version}";
    internal const string Restore = Version + "/restore";
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    internal const string Description =
        "Rules decide which rows and columns end users signed in to the project's tenant may read and write: one set per database, "
        + "with a condition per table and operation. A database without rules denies every end-user request; project credentials are not limited by rules. "
        + ProjectApiRoute.IdentityDescription;

    internal const string WriteDescription =
        " Send If-Match: \"<current version>\", or If-None-Match: * when the database has no rules; without either the answer is 428 precondition_required, "
        + "and a stale version answers 412 version_mismatch with the current ETag. Every condition is compiled against the current schema first; "
        + "any problem answers 400 invalid_rules listing every problem in details.problems. Sent to the database's home region (421 wrong_region elsewhere). ";

    internal static readonly NotFound UnknownVersion = new("the database has no such rules version", "unknown_version");

    internal static async ValueTask<object> DatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(context, management, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant => tenant,
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Unavailable error => ErrorResponse.From(error),
        };

    internal static async ValueTask<IResult?> ElsewhereAsync(HttpContext context, IManagementService management, TerraBaseOptions options) =>
        await DatabaseRegions.ElsewhereAsync(context, management, options.Region).ConfigureAwait(false) switch
        {
            Done => null,
            DatabaseRegions.Misdirected elsewhere => elsewhere,
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Unavailable error => ErrorResponse.From(error),
        };

    internal static RulesPrecondition? Precondition(HttpRequest request)
    {
        var match = request.Headers.IfMatch;
        if (match.Count > 0)
        {
            return new RulesIfMatch(match is [{ Length: > 2 } tag] && tag[0] == '"' && tag[^1] == '"' ? tag[1..^1] : match.ToString());
        }

        return request.Headers.IfNoneMatch is ["*"] ? new RulesIfNone() : null;
    }

    internal static IResult Required() => new Status(StatusCodes.Status428PreconditionRequired, "precondition_required",
        "send If-Match: \"<current version>\", or If-None-Match: * when the database has no rules");

    internal static readonly Validation BadPage = new($"after must be one rules version and limit one whole number from 1 to {MaxLimit}");

    internal static RulesPage? Page(HttpRequest request)
    {
        var query = request.Query;
        string? after = null;
        var limit = DefaultLimit;
        if (query.TryGetValue("after", out var afters))
        {
            if (afters is not [{ } given] || !EntityIds.IsValid(given))
            {
                return null;
            }

            after = given;
        }

        return query.TryGetValue("limit", out var limits)
            && (limits is not [{ } text] || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit)
            ? null
            : new RulesPage(after, limit);
    }

    internal static IResult Body(HttpContext context, StoredRules rules, IReadOnlyList<RulesProblem>? problems, IReadOnlyList<RulesProblem>? warnings)
    {
        context.Response.Headers.ETag = $"\"{rules.Version}\"";
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(new RulesBody(rules.Version, rules.Rules.Tables, problems, warnings, rules.UpdatedAt, rules.UpdatedBy),
            RulesJsonContext.Default.RulesBody);
    }

    internal static IResult Deployed(HttpContext context, RulesDeployResult result)
    {
        switch (result)
        {
            case DeployedRules deployed:
                return Body(context, deployed.Rules, null, deployed.Warnings);
            case InvalidRules invalid:
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Json(new RulesErrorBody(new RulesErrorDetail("invalid_rules", "the rules do not fit the database; see details.problems",
                    new RulesErrorDetails(invalid.Problems))), RulesJsonContext.Default.RulesErrorBody, statusCode: StatusCodes.Status400BadRequest);
            case StaleRules stale:
                if (stale.Current is { } current)
                {
                    context.Response.Headers.ETag = $"\"{current}\"";
                }

                return ErrorResponse.From(new PreconditionFailed("the rules changed since the version you named; read them again", "version_mismatch"));
            case NotFound error:
                return ErrorResponse.From(error);
            case Unavailable error:
                return ErrorResponse.From(error);
        }

        throw new UnreachableException();
    }

    private sealed class Status(int status, string code, string message) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => BadRequests.WriteAsync(httpContext, status, code, message, op: null);
    }
}
