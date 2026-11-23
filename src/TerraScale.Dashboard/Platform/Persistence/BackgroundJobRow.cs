namespace TerraScale.Dashboard.Platform.Persistence;

public sealed record BackgroundJobRow
{
    public required string Id { get; init; }
    public required string Owner { get; set; }
    public required string Checkpoint { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime LeaseUntil { get; set; }
    public int Failures { get; set; }
    public DateTime? AttemptedAt { get; set; }
    public DateTime? SucceededAt { get; set; }
    public string? Cursor { get; set; }
    public string? NextCursor { get; set; }
    public string? DeliveryId { get; set; }
    public byte[]? Payload { get; set; }
    public string? OriginalOwner { get; set; }
    public string? OriginalCheckpoint { get; set; }
    public DateTime DatabaseNow { get; init; }

    public string? PayloadHex => Payload is null ? null : Convert.ToHexString(Payload);

    public bool Leased => LeaseUntil > DatabaseNow;
}
