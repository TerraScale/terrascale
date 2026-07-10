using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Queue kinds accepted by project queue creation endpoints.
/// </summary>
public static class TerraScaleQueueKinds
{
    /// <summary>
    /// A durable competing-consumer work queue.
    /// </summary>
    public const string Queue = "queue";

    /// <summary>
    /// A durable publish/subscribe topic.
    /// </summary>
    public const string Topic = "topic";
}

/// <summary>
/// Request body for creating a project-scoped queue or topic.
/// </summary>
public sealed record TerraScaleCreateProjectQueueRequest(
    string Name,
    string Kind = TerraScaleQueueKinds.Queue,
    string? Description = null,
    long? RetentionSeconds = null,
    int? MaxMessageSizeBytes = null,
    bool Fifo = false,
    int? PartitionCount = null,
    long? DuplicateDetectionSeconds = null);

/// <summary>
/// Request body for publishing a JSON message to a project queue or topic.
/// </summary>
public sealed record TerraScalePublishProjectQueueMessageRequest<TData>(
    TData Data,
    string? IdempotencyKey = null,
    string? PartitionKey = null,
    long? DelaySeconds = null,
    long? TimeToLiveSeconds = null);

/// <summary>
/// Project-scoped queue or topic metadata and current statistics.
/// </summary>
public sealed record TerraScaleProjectQueue(
    string QueueId,
    string ProjectId,
    string Name,
    string Kind,
    long Messages,
    long StorageBytes,
    int ConsumerCount,
    int Replicas,
    bool Fifo,
    int PartitionCount,
    long? RetentionSeconds,
    DateTimeOffset CreatedAt);

/// <summary>
/// Result of publishing one project queue message.
/// </summary>
public sealed record TerraScaleProjectQueuePublishResult(
    string MessageId,
    ulong Sequence,
    DateTimeOffset PublishedAt,
    bool Duplicate);

/// <summary>
/// A non-destructively inspected project queue message.
/// </summary>
public sealed record TerraScaleProjectQueuePeekMessage(
    string MessageId,
    JsonElement Data,
    string ContentType,
    IReadOnlyDictionary<string, string> Headers,
    ulong Sequence,
    DateTimeOffset? CreatedAt);

/// <summary>
/// Result of purging all currently available messages from a project queue.
/// </summary>
public sealed record TerraScaleProjectQueuePurgeResult(
    string QueueName,
    long PurgedMessages);

/// <summary>
/// Delivery guarantees reported by the TerraScale queue consumer API.
/// </summary>
public static class TerraScaleQueueDeliveryGuarantees
{
    /// <summary>
    /// A message can be delivered more than once until it is completed or dead-lettered.
    /// </summary>
    public const string AtLeastOnce = "at-least-once";
}

/// <summary>
/// Options for receiving a bounded batch from a durable queue consumer.
/// </summary>
public sealed record TerraScaleReceiveProjectQueueMessagesRequest(
    string ConsumerName,
    int MaxMessages = 1,
    double? WaitSeconds = null,
    double? LockSeconds = null);

/// <summary>
/// A durable, opaque receipt together with the regional cell that owns its message lock.
/// </summary>
public sealed record TerraScaleProjectQueueReceipt(string Receipt, string Region);

/// <summary>
/// A queue message locked for an at-least-once consumer delivery.
/// </summary>
public sealed record TerraScaleProjectQueueReceivedMessage(
    string Receipt,
    string MessageId,
    JsonElement Data,
    string ContentType,
    IReadOnlyDictionary<string, string> Headers,
    ulong StreamSequence,
    ulong ConsumerSequence,
    ulong DeliveryAttempt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset LockedUntil,
    string Region,
    string DeliveryGuarantee)
{
    /// <summary>
    /// Creates the receipt value accepted by complete, abandon, dead-letter, and renew-lock operations.
    /// </summary>
    public TerraScaleProjectQueueReceipt DeliveryReceipt => new(Receipt, Region);
}

/// <summary>
/// Result of sending a terminal or abandon acknowledgement for a queue message.
/// </summary>
public sealed record TerraScaleProjectQueueReceiptActionResult(
    string MessageId,
    string Operation,
    bool Accepted,
    bool SafeToRetry,
    string Region,
    string DeliveryGuarantee);

/// <summary>
/// Result of extending a message lock. The returned receipt replaces the prior receipt.
/// </summary>
public sealed record TerraScaleProjectQueueRenewLockResult(
    string Receipt,
    string MessageId,
    DateTimeOffset LockedUntil,
    string Region,
    string DeliveryGuarantee)
{
    /// <summary>
    /// Creates the renewed receipt value accepted by later acknowledgement operations.
    /// </summary>
    public TerraScaleProjectQueueReceipt DeliveryReceipt => new(Receipt, Region);
}

internal sealed record TerraScaleProjectQueueReceiptRequest(string Receipt);

internal sealed record TerraScaleAbandonProjectQueueMessageRequest(
    string Receipt,
    double? DelaySeconds);
