using TerraBase.Features.HomeApply;

namespace TerraBase.Features.LocalAck;

/// <summary>
/// One local transaction the region's acking node published to its push stream (sequence <see cref="Sequence"/>), held by
/// its in-region partner until the home has it (docs/terrabase/in-region-ack.md). <see cref="Version"/> is 1.
/// </summary>
internal sealed record HeldTransaction(
    int Version, string? Origin, string? Home, string? Organization, string? Project, string? Database, ulong Sequence, RegionTransaction? Transaction);

/// <summary>The home holds every transaction of the database up to <see cref="Through"/>: the partner drops them.</summary>
internal sealed record HoldRelease(string? Origin, string? Organization, string? Project, string? Database, ulong Through);
