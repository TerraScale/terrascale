using System.Text.Json.Serialization.Metadata;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Public atomic authentication persistence and explicit transaction identities.</summary>
public sealed class TerraScaleAuthPersistenceClient(TerraScaleClient client)
{
    public Task<AuthResult> InsertUniqueAsync(string database, UniqueInsert request, string idempotencyKey, string? transactionId = null, CancellationToken cancellationToken = default) =>
        Send(database, "insertUnique", request, TerraScaleContractJsonContext.Default.UniqueInsert, idempotencyKey, transactionId, cancellationToken);

    public Task<AuthResult> CompareAndSwapAsync(string database, ConditionalUpdate request, string idempotencyKey, string? transactionId = null, CancellationToken cancellationToken = default) =>
        Send(database, "compareAndSwap", request, TerraScaleContractJsonContext.Default.ConditionalUpdate, idempotencyKey, transactionId, cancellationToken);

    public Task<AuthResult> ConsumeOneAsync(string database, Consume request, string idempotencyKey, string? transactionId = null, CancellationToken cancellationToken = default) =>
        Send(database, "consumeOne", request, TerraScaleContractJsonContext.Default.Consume, idempotencyKey, transactionId, cancellationToken);

    public Task<AuthResult> CreateIndexAsync(string database, UniqueIndex request, string idempotencyKey, string? transactionId = null, CancellationToken cancellationToken = default) =>
        Send(database, "index", request, TerraScaleContractJsonContext.Default.UniqueIndex, idempotencyKey, transactionId, cancellationToken);

    public Task<AuthResult> FindPageAsync(string database, PageQuery request, string idempotencyKey, string? transactionId = null, CancellationToken cancellationToken = default) =>
        Send(database, "findPage", request, TerraScaleContractJsonContext.Default.PageQuery, idempotencyKey, transactionId, cancellationToken);

    public Task<AuthResult> GetAsync(string database, string model, string id, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, Route(database) + "/records/" + TerraScaleManagementPaths.Escape(model) + "/" + TerraScaleManagementPaths.Escape(id),
            TerraScaleContractJsonContext.Default.AuthResult, cancellationToken);

    public Task<AuthResult> BeginTransactionAsync(string database, string transactionId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Transaction(database, "begin", transactionId, idempotencyKey, cancellationToken);

    public Task<AuthResult> CommitTransactionAsync(string database, string transactionId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Transaction(database, "commit", transactionId, idempotencyKey, cancellationToken);

    public Task<AuthResult> RollbackTransactionAsync(string database, string transactionId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Transaction(database, "rollback", transactionId, idempotencyKey, cancellationToken);

    private Task<AuthResult> Transaction(string database, string action, string transactionId, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        return Send(database, "transactions/" + action, new TransactionIdentity(transactionId), TerraScaleContractJsonContext.Default.TransactionIdentity, idempotencyKey, null, cancellationToken);
    }

    private Task<AuthResult> Send<T>(string database, string action, T request, JsonTypeInfo<T> metadata, string idempotencyKey, string? transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (transactionId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        return client.SendAuthPersistenceAsync(HttpMethod.Post, Route(database) + "/" + action, request, metadata, idempotencyKey, transactionId, cancellationToken);
    }

    private static string Route(string database) => TerraScaleManagementPaths.Database(database) + "/auth";
}
