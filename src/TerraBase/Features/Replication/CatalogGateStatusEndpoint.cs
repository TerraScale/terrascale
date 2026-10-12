using TerraScale.Endpoints;

namespace TerraBase.Features.Replication;

/// <summary>This machine's catalog gate, as another machine's gate reads it.</summary>
/// <param name="Open">Whether the gate is open.</param>
/// <param name="Trusted">Whether the catalog is known not to be a restored older copy (<see cref="ICatalogGate.Trusted"/>).</param>
public sealed record CatalogGateStatus(bool Open, bool Trusted);

/// <summary>
/// <c>GET /catalog-gate</c>: whether this machine's catalog gate is open and its catalog trusted. Internal listener only,
/// unauthenticated like the other probes: the other machines' gates read it, and it carries no tenant data.
/// </summary>
public sealed class CatalogGateStatusEndpoint(ICatalogGate gate) : EndpointWithoutRequest<CatalogGateStatus>
{
    /// <summary>The route.</summary>
    public const string Path = "/catalog-gate";

    /// <inheritdoc />
    public override void Configure()
    {
        Get(Path);
        Name("catalogGate");
        Summary("This machine's catalog gate");
        Description("Internal listener only. open: whether the catalog gate is open; trusted: whether the catalog is known not to be a restored older copy (another machine counts a pull from this one only then).");
        Tags("Administration");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<CatalogGateStatus> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult(new CatalogGateStatus(gate.IsOpen, gate.Trusted));
    }
}
