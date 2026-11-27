namespace TerraBase.Continuity;

public sealed record S3ArchiveSettings(Uri Endpoint, string Region, string Bucket, string AccessKeyId, string SecretAccessKey)
{
    public override string ToString() => string.Concat("s3 archive ", Bucket, " at ", Endpoint.Authority);
}

public sealed record ContinuityOptions
{
    public S3ArchiveSettings? Archive { get; init; }

    public TimeSpan ArchiveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan SnapshotFailoverGrace { get; init; } = TimeSpan.FromMinutes(15);

    public int ArchiveParallelism { get; init; } = 4;

    public int LocalSnapshotsRetained { get; init; } = 2;

    public int ChangefeedSubscriberCapacity { get; init; } = 1024;

    public int OperationQueueCapacity { get; init; } = 64;

    public int RetainedOperations { get; init; } = 32;

    public bool IsBackupEnabled => Archive is not null;

    public ContinuityOptions Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ArchiveInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SnapshotInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(SnapshotFailoverGrace, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ArchiveParallelism);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LocalSnapshotsRetained);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChangefeedSubscriberCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(OperationQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RetainedOperations);
        return this;
    }
}
