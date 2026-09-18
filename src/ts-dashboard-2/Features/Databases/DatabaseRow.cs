namespace TerraScale.Dashboard2.Features.Databases;

public sealed record DatabaseRow(
    string Id,
    string Name,
    string Regions,
    string? BackupRegion,
    string Replication,
    string Status,
    string StatusTone,
    bool Provisioning,
    string? OperationId,
    string CreatedAt,
    int Version);
