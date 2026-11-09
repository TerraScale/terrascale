namespace TerraBase.Features.LocalAck;

// Version is 1. The in-region partner holds the transaction until the home has it (docs/terrabase/in-region-ack.md).
internal sealed record HeldTransaction(
    int Version, string? Origin, string? Home, string? Organization, string? Project, string? Database, ulong Sequence, byte[]? Payload);

internal sealed record HoldRelease(string? Origin, string? Organization, string? Project, string? Database, ulong Through);
