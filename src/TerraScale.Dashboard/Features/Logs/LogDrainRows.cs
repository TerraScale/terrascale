using Dapper;

namespace TerraScale.Dashboard.Features.Logs;

public sealed record LogDrainRow
{
    public string? OriginalRevision { get; set; }

    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required string Revision { get; set; }

    public required string Url { get; set; }

    public string? HeaderName { get; set; }

    public string? HeaderNonce { get; set; }

    public string? HeaderCiphertext { get; set; }

    public string? HeaderTag { get; set; }

    public required DateTime CreatedAt { get; init; }

    public DateTime? LastSuccessAt { get; set; }

    public DateTime? LastErrorAt { get; set; }

    public int? LastErrorStatus { get; set; }

    public DateTime? LastGapAt { get; set; }

    [DbValue(Ignore = true)]
    public List<LogDrainCursorRow> Cursors { get; init; } = [];
}

public sealed record LogDrainCursorRow
{
    public required string Id { get; init; }

    public required string DrainId { get; init; }

    public required string DatabaseId { get; init; }

    public required string Cursor { get; set; }
}
