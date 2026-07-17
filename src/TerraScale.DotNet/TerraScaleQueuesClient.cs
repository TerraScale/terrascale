namespace TerraScale.DotNet;

/// <summary>
/// Client for project-scoped queue and topic endpoints.
/// </summary>
public sealed class TerraScaleQueuesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleQueuesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists every queue and topic in a project.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleProjectQueue>> ListAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return client.SendAsync<IReadOnlyList<TerraScaleProjectQueue>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectQueuesList(projectId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates a project queue or topic with the API defaults for the selected kind.
    /// </summary>
    public Task<TerraScaleProjectQueue> CreateAsync(
        string projectId,
        string name,
        string kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return CreateAsync(
            projectId,
            new TerraScaleCreateProjectQueueRequest(name.Trim(), kind),
            cancellationToken);
    }

    /// <summary>
    /// Creates a project queue or topic with explicit delivery and retention options.
    /// </summary>
    public Task<TerraScaleProjectQueue> CreateAsync(
        string projectId,
        TerraScaleCreateProjectQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);

        var kind = request.Kind.Trim().ToLowerInvariant();
        if (kind is not TerraScaleQueueKinds.Queue and not TerraScaleQueueKinds.Topic)
        {
            throw new ArgumentException(
                $"Queue kind must be '{TerraScaleQueueKinds.Queue}' or '{TerraScaleQueueKinds.Topic}'.",
                nameof(request));
        }

        return client.SendAsync<TerraScaleProjectQueue>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectQueuesCreate(projectId),
            request with
            {
                Name = request.Name.Trim(),
                Kind = kind
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets one project queue or topic by id.
    /// </summary>
    public Task<TerraScaleProjectQueue> GetAsync(
        string projectId,
        string queueId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(projectId, queueId);
        return client.SendAsync<TerraScaleProjectQueue>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectQueueGet(projectId, queueId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Deletes one project queue or topic and its retained messages.
    /// </summary>
    public Task<bool> DeleteAsync(
        string projectId,
        string queueId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(projectId, queueId);
        return client.SendAsync<bool>(
            HttpMethod.Delete,
            TerraScaleApiRoutes.ProjectQueueDelete(projectId, queueId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Purges all currently available messages while keeping the queue definition.
    /// </summary>
    public Task<TerraScaleProjectQueuePurgeResult> PurgeAsync(
        string projectId,
        string queueId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(projectId, queueId);
        return client.SendAsync<TerraScaleProjectQueuePurgeResult>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectQueuePurge(projectId, queueId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Publishes a JSON-serializable message using the queue API defaults.
    /// </summary>
    public Task<TerraScaleProjectQueuePublishResult> PublishAsync<TData>(
        string projectId,
        string queueId,
        TData data,
        CancellationToken cancellationToken = default) =>
        PublishAsync(
            projectId,
            queueId,
            new TerraScalePublishProjectQueueMessageRequest<TData>(data),
            cancellationToken);

    /// <summary>
    /// Publishes a JSON-serializable message with idempotency, partition, delay, and TTL options.
    /// </summary>
    public Task<TerraScaleProjectQueuePublishResult> PublishAsync<TData>(
        string projectId,
        string queueId,
        TerraScalePublishProjectQueueMessageRequest<TData> request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(projectId, queueId);
        ArgumentNullException.ThrowIfNull(request);
        return client.SendAsync<TerraScaleProjectQueuePublishResult>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectQueuePublish(projectId, queueId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Non-destructively inspects up to 100 currently available messages.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleProjectQueuePeekMessage>> PeekAsync(
        string projectId,
        string queueId,
        int maxMessages = 10,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(projectId, queueId);
        if (maxMessages is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMessages),
                "Maximum messages must be between 1 and 100.");
        }

        return client.SendAsync<IReadOnlyList<TerraScaleProjectQueuePeekMessage>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ProjectQueuePeek(projectId, queueId, maxMessages),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Receives and locks up to 100 messages for a durable at-least-once consumer.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleProjectQueueReceivedMessage>> ReceiveAsync(
        string projectId,
        string queueId,
        TerraScaleReceiveProjectQueueMessagesRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(projectId, queueId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConsumerName);
        if (request.MaxMessages is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum messages must be between 1 and 100.");
        }

        return client.SendAsync<IReadOnlyList<TerraScaleProjectQueueReceivedMessage>>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectQueueReceive(projectId, queueId),
            request with { ConsumerName = request.ConsumerName.Trim() },
            cancellationToken);
    }

    /// <summary>
    /// Completes a delivery. Repeating the same opaque receipt is safe, while message delivery remains at-least-once.
    /// </summary>
    public Task<TerraScaleProjectQueueReceiptActionResult> CompleteAsync(
        string projectId,
        string queueId,
        TerraScaleProjectQueueReceipt receipt,
        CancellationToken cancellationToken = default) =>
        SendReceiptActionAsync(
            projectId,
            queueId,
            receipt,
            TerraScaleApiRoutes.ProjectQueueComplete(projectId, queueId),
            cancellationToken);

    /// <summary>
    /// Releases a delivery for redelivery, optionally after a delay.
    /// </summary>
    public Task<TerraScaleProjectQueueReceiptActionResult> AbandonAsync(
        string projectId,
        string queueId,
        TerraScaleProjectQueueReceipt receipt,
        double? delaySeconds = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReceipt(projectId, queueId, receipt);
        return client.SendAsync<TerraScaleProjectQueueReceiptActionResult>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectQueueAbandon(projectId, queueId),
            new TerraScaleAbandonProjectQueueMessageRequest(receipt.Receipt, delaySeconds),
            receipt.Region,
            cancellationToken);
    }

    /// <summary>
    /// Stops redelivery of a failed message without reporting successful processing.
    /// </summary>
    public Task<TerraScaleProjectQueueReceiptActionResult> DeadLetterAsync(
        string projectId,
        string queueId,
        TerraScaleProjectQueueReceipt receipt,
        CancellationToken cancellationToken = default) =>
        SendReceiptActionAsync(
            projectId,
            queueId,
            receipt,
            TerraScaleApiRoutes.ProjectQueueDeadLetter(projectId, queueId),
            cancellationToken);

    /// <summary>
    /// Extends a delivery lock and returns a replacement receipt with a fresh expiry.
    /// </summary>
    public Task<TerraScaleProjectQueueRenewLockResult> RenewLockAsync(
        string projectId,
        string queueId,
        TerraScaleProjectQueueReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ValidateReceipt(projectId, queueId, receipt);
        return client.SendAsync<TerraScaleProjectQueueRenewLockResult>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ProjectQueueRenewLock(projectId, queueId),
            new TerraScaleProjectQueueReceiptRequest(receipt.Receipt),
            receipt.Region,
            cancellationToken);
    }

    private Task<TerraScaleProjectQueueReceiptActionResult> SendReceiptActionAsync(
        string projectId,
        string queueId,
        TerraScaleProjectQueueReceipt receipt,
        string route,
        CancellationToken cancellationToken)
    {
        ValidateReceipt(projectId, queueId, receipt);
        return client.SendAsync<TerraScaleProjectQueueReceiptActionResult>(
            HttpMethod.Post,
            route,
            new TerraScaleProjectQueueReceiptRequest(receipt.Receipt),
            receipt.Region,
            cancellationToken);
    }

    private static void ValidateReceipt(
        string projectId,
        string queueId,
        TerraScaleProjectQueueReceipt receipt)
    {
        ValidateIdentifiers(projectId, queueId);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.Receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.Region);
    }

    private static void ValidateIdentifiers(string projectId, string queueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
    }
}
