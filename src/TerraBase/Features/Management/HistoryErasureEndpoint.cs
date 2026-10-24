using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>
/// <c>PUT /admin/history-erasures/{erasure}</c>: ts-auth's shared-history erasure handoff (docs/terrabase/history-erasure.md).
/// Internal listener only, behind the peer credential. The scope is fixed: ts-auth's database and SYSTEM; the caller names
/// only the random erasure id of its deletion job (whose auth request document names the fence), never a database.
/// </summary>
internal sealed class HistoryErasureEndpoint(HistoryErasures erasures) : Endpoint<HistoryErasureRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/admin/history-erasures/{erasure}");
        Name("requestHistoryErasure");
        Summary("Requests or advances a shared-history erasure");
        Description("Version 1. Creates the permanent erasure authority from auth's original request time (409 erasure_conflict for another time), "
            + "then answers its status from every holder's receipt: 202 fencing, settling, reverify (with this machine's settlementToken once it covered every "
            + "holder's fence marker), rewriting, residue or backup; 200 complete. Presenting the token after auth re-verified captures the final cut; "
            + "409 settlement_stale when it is not this machine's current token.");
        Tags("Administration");
        Produces<HistoryErasureStatus>();
        Produces<HistoryErasureStatus>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(HistoryErasureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var erasure = HttpContext.Request.RouteValues["erasure"] as string;
        if (request.Version != 1 || !EntityIds.IsValid(erasure) || request.RequestedAt == default
            || request.SettlementToken is not null && !EntityIds.IsValid(request.SettlementToken))
        {
            return ErrorResponse.From(new Validation("version must be 1; the erasure id and settlementToken must be canonical uppercase ULIDs; requestedAt is required"));
        }

        return await erasures.RequestAsync(erasure, request.RequestedAt, request.SettlementToken, cancellationToken).ConfigureAwait(false) switch
        {
            HistoryErasureStatus { Status: HistoryErasure.Complete } complete => TypedResults.Ok(complete),
            HistoryErasureStatus pending => TypedResults.Json(pending, TerraBaseJsonContext.Default.HistoryErasureStatus, statusCode: StatusCodes.Status202Accepted),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
