namespace TerraBase.Features.LocalAck;

internal sealed record HeldTransaction(
    int Version, string? Origin, string? Home, string? Organization, string? Project, string? Database, ulong Sequence, byte[]? Payload);

internal sealed record HoldRelease(string? Origin, string? Organization, string? Project, string? Database, ulong Through);
