using TerraBase.Errors;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replication;

/// <summary>Why an operator opens the catalog gate without the missing verifications.</summary>
/// <param name="Reason">Recorded with the override; 1 to 500 characters.</param>
public sealed record OpenCatalogGateRequest(string Reason);

/// <summary>The machines the catalog was not verified against when the gate opened (empty when it was already open).</summary>
/// <param name="Unverified">Their machine ids.</param>
public sealed record OpenCatalogGateBody(IReadOnlyList<string> Unverified);

/// <summary>
/// <c>POST /admin/catalog-gate/open</c>: opens this machine's restore gate (<see cref="ICatalogGate"/>) while other started
/// machines cannot be reached to verify its catalog. Internal listener only (the operator, through <c>fly ssh</c> or the
/// private network); never automatic. Each call is recorded: a warning log and a catalog document under
/// <c>machines/&lt;machine&gt;/catalog_gate_overrides</c>.
/// </summary>
public sealed class OpenCatalogGateEndpoint(ICatalogGate gate) : Endpoint<OpenCatalogGateRequest, IResult>
{
    private const int MaxReasonLength = 500;

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/catalog-gate/open");
        Name("openCatalogGate");
        Summary("Opens the catalog restore gate without the missing verifications");
        Description("Serves the catalog although some started machines could not be pulled from since this start, so entries a restored catalog lost (revoked keys, removed members, deleted ids) may read live until anti-entropy repairs them. Requires a reason; recorded in the log and the catalog.");
        Tags("Administration");
        Produces<OpenCatalogGateBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(OpenCatalogGateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Reason) || request.Reason.Length > MaxReasonLength)
        {
            return ErrorResponse.From(new Validation("reason must be 1 to 500 characters"));
        }

        return TypedResults.Ok(new OpenCatalogGateBody(await gate.OverrideAsync(request.Reason, cancellationToken).ConfigureAwait(false)));
    }
}
