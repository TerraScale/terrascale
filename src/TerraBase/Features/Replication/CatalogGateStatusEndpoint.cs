using TerraScale.Endpoints;

namespace TerraBase.Features.Replication;

/// <summary>This machine's catalog gate, as another machine's gate reads it.</summary>
/// <param name="Open">Whether the gate is open.</param>
/// <param name="Trusted">Whether the catalog is known not to be a restored older copy (<see cref="ICatalogGate.Trusted"/>).</param>
/// <param name="Pending">What the gate still waits for: machine ids and reasons.</param>
/// <param name="Digest">A digest of the whole catalog (<see cref="ICatalogGate.DigestAsync"/>): equal on converged machines.</param>
public sealed record CatalogGateStatus(bool Open, bool Trusted, IReadOnlyList<string> Pending, string? Digest);

/// <summary>
/// <c>GET /catalog-gate</c>: whether this machine's catalog gate is open and its catalog trusted. Internal listener only,
/// records peer credential verification like the other internal probes: the other machines' gates read it.
/// </summary>
public sealed class CatalogGateStatusEndpoint(ICatalogGate gate) : EndpointWithoutRequest<CatalogGateStatus>
{
    /// <summary>The route.</summary>
    public const string Path = "/v1/catalog-gate";

    /// <inheritdoc />
    public override void Configure()
    {
        Get(Path);
        Name("catalogGate");
        Summary("This machine's catalog gate");
        Description("Internal listener only. open: whether the catalog gate is open; trusted: whether the catalog is known not to be a restored older copy (another machine counts a pull from this one only then); pending: what the gate still waits for (machine ids and reasons); digest: SHA-256 of the catalog's anti-entropy bucket digests, equal on machines whose catalogs converged (docs/terrabase/restore-drill.md).");
        Tags("Administration");
        AllowAnonymous();
        Metadata(TerraBase.Hosting.ListenerMetadata.Internal);
    }

    /// <inheritdoc />
    public override async ValueTask<CatalogGateStatus> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return new CatalogGateStatus(gate.IsOpen, gate.Trusted, gate.Waiting, await gate.DigestAsync(cancellationToken).ConfigureAwait(false));
    }
}
