// <copyright file="Surface.cs" company="TerraScale">
// TerraBase (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

using System.Text.Json;

/// <summary>
/// The typed operation surface both CLI modes serve: the host mode
/// drives the API host's typed HTTP surface; the direct mode drives
/// the same operation set against a local engine process through the
/// SE-14 TBEP client. Commands are mode-agnostic — they speak this
/// surface only, so the two modes cannot drift apart.
/// </summary>
public interface ITerraBaseSurface : IAsyncDisposable
{
    /// <summary>Reads the engine and topology summary (the <c>status</c> command).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The status report.</returns>
    Task<StatusReport> ReadStatusAsync(CancellationToken cancellationToken);

    /// <summary>Reads one document.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="document">The document id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The read outcome.</returns>
    Task<DocumentReadOutcome> ReadDocumentAsync(string collection, string document, CancellationToken cancellationToken);

    /// <summary>Writes one document body (a mutation; the idempotency key is the retry identity).</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="document">The document id.</param>
    /// <param name="body">The plain JSON body.</param>
    /// <param name="expected">The expected-version conditional: <c>null</c>, "any", or an exact version.</param>
    /// <param name="idempotencyKey">The stable retry identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The write outcome.</returns>
    Task<WriteOutcome> WriteDocumentAsync(string collection, string document, JsonElement body, string? expected, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Deletes one document (a mutation; the idempotency key is the retry identity).</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="document">The document id.</param>
    /// <param name="expected">The expected-version conditional.</param>
    /// <param name="idempotencyKey">The stable retry identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The delete outcome.</returns>
    Task<WriteOutcome> DeleteDocumentAsync(string collection, string document, string? expected, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Executes one TQL query.</summary>
    /// <param name="text">The TQL text.</param>
    /// <param name="maxItems">The item bound.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The query outcome.</returns>
    Task<QueryOutcome> ExecuteQueryAsync(string text, uint maxItems, CancellationToken cancellationToken);

    /// <summary>Sends the query-cancellation live signal.</summary>
    /// <param name="queryId">The in-flight query identity (caller-tracked).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The cancel outcome.</returns>
    Task<string> CancelQueryAsync(ulong queryId, CancellationToken cancellationToken);

    /// <summary>Opens the Change Event Backbone tail (the <c>changes</c> command).</summary>
    /// <param name="from">The inclusive replay start sequence.</param>
    /// <param name="collections">The collection filter, or <c>null</c> for every collection.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The tail; its frames carry the ordered change events and content-free Change Notifications.</returns>
    Task<IChangesTail> OpenChangesAsync(ulong from, string[]? collections, CancellationToken cancellationToken);

    /// <summary>Reads the sync-session diagnostics: the host registry, or the engine's truthful counters on a direct channel.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sessions report.</returns>
    Task<SessionsReport> ReadSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Reads the lifecycle audit log (the API host's surface).</summary>
    /// <param name="limit">The bounded record count.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The audit records JSON.</returns>
    Task<JsonElement> ReadAuditAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Reads the host metrics (the API host's surface).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The metrics JSON.</returns>
    Task<JsonElement> ReadMetricsAsync(CancellationToken cancellationToken);

    /// <summary>Reads one collection's recorded schema state (the <c>schema status</c> command).</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The schema state report.</returns>
    Task<SchemaStateReport> ReadSchemaStateAsync(string collection, CancellationToken cancellationToken);

    /// <summary>Validates one document value against the recorded schema (the <c>schema validate</c> command).</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="document">The document value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The typed validation report.</returns>
    Task<SchemaCheckReport> ValidateSchemaAsync(string collection, JsonElement document, CancellationToken cancellationToken);

    /// <summary>Computes one migration contract's dry run (the <c>migration dry-run</c> command).</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="contract">The typed migration contract.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The plan, with its evidence, in a stable machine-readable shape.</returns>
    Task<JsonElement> MigrationDryRunAsync(string collection, JsonElement contract, CancellationToken cancellationToken);

    /// <summary>Applies one migration contract to completion (the <c>migration apply</c> command).</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="contract">The typed migration contract.</param>
    /// <param name="acknowledgeNotRollbackable">The explicit not-rollbackable acknowledgment.</param>
    /// <param name="idempotencyKey">The stable retry identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The recorded receipt, in a stable machine-readable shape.</returns>
    Task<JsonElement> MigrationApplyAsync(string collection, JsonElement contract, bool acknowledgeNotRollbackable, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Begins the engine's graceful shutdown.</summary>
    /// <param name="reason">The reason: <c>operator</c> or <c>supervisor</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The shutdown report.</returns>
    Task<ShutdownReport> ShutdownAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>The recorded schema state of one collection: the enforced version and the rule count.</summary>
/// <param name="EnforcedVersion">The enforced version, or <c>null</c> for a schema-free collection.</param>
/// <param name="RuleCount">The recorded rule count.</param>
/// <param name="MigrationKind">The migration state kind: none, in_progress, rolling_back, or complete.</param>
public sealed record SchemaStateReport(ulong? EnforcedVersion, uint RuleCount, string MigrationKind);

/// <summary>The typed validation answer of one document value.</summary>
/// <param name="EnforcedVersion">The version the validation enforced.</param>
/// <param name="Valid">Whether the value satisfies the recorded schema.</param>
/// <param name="Violations">The typed violations, in canonical order.</param>
public sealed record SchemaCheckReport(ulong? EnforcedVersion, bool Valid, IReadOnlyList<SchemaViolationRow> Violations);

/// <summary>One typed policy violation: a field and a kind, never a field value.</summary>
/// <param name="Field">The violated field name.</param>
/// <param name="Kind">The snake_case violation kind.</param>
public sealed record SchemaViolationRow(string Field, string Kind);

/// <summary>One frame of the Change Event Backbone tail.</summary>
/// <param name="Kind">The frame kind: <c>change-event</c>, <c>change-notification</c>, <c>drop</c>, or <c>closed</c>.</param>
/// <param name="Sequence">The change-event sequence, when the frame carries one.</param>
/// <param name="ChangeKind">The change kind (put/delete), for a change event.</param>
/// <param name="Collection">The collection.</param>
/// <param name="Document">The document id, for a change event.</param>
/// <param name="OperationId">The operation id.</param>
/// <param name="ResumeFrom">The dropped region's boundary, for a drop frame.</param>
/// <param name="Reason">The close reason, for a closed frame.</param>
/// <param name="NextSequence">The batch's next sequence, for a change-event frame of a replay batch.</param>
public sealed record TailFrame(
    string Kind,
    ulong? Sequence,
    string? ChangeKind,
    string? Collection,
    string? Document,
    string? OperationId,
    ulong? ResumeFrom,
    string? Reason,
    ulong? NextSequence);

/// <summary>The Change Event Backbone tail: the ordered frames of one subscription.</summary>
public interface IChangesTail : IAsyncDisposable
{
    /// <summary>Reads the next frame; <c>null</c> when the tail ended.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The frame, or <c>null</c> at a clean end.</returns>
    Task<TailFrame?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Closes the subscription (the bounded idle close of a non-following tail).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the subscription is closed.</returns>
    Task CloseAsync(CancellationToken cancellationToken);
}

/// <summary>The engine and topology summary of the <c>status</c> command.</summary>
/// <param name="HealthStatus">The composite health: <c>serving</c> or <c>unavailable</c>.</param>
/// <param name="EngineState">The engine's own state, when reachable.</param>
/// <param name="Sessions">The engine's session count, when reachable.</param>
/// <param name="Subscriptions">The engine's subscription count, when reachable.</param>
/// <param name="FramesServed">The engine's frames-served counter, when reachable.</param>
/// <param name="Generation">The engine generation (the host's supervision; 1 for a direct channel).</param>
/// <param name="Restarts">The completed restart count.</param>
/// <param name="Detail">The machine-readable detail of an unhealthy endpoint.</param>
/// <param name="RegistryVersion">The TBEP registry version of the serving engine.</param>
/// <param name="Scopes">The engine's scope count.</param>
/// <param name="Binding">The engine's binding kind (for example <c>LocalUds</c>).</param>
/// <param name="Subsystems">The serving subsystems.</param>
/// <param name="EngineTopologyAvailable">Whether the engine publishes a TopologyView at this binding.</param>
public sealed record StatusReport(
    string HealthStatus,
    string? EngineState,
    uint? Sessions,
    uint? Subscriptions,
    ulong? FramesServed,
    int Generation,
    int Restarts,
    string? Detail,
    string? RegistryVersion,
    uint? Scopes,
    string? Binding,
    string[]? Subsystems,
    bool? EngineTopologyAvailable);

/// <summary>The document read outcome: the identity, the version, and the plain JSON body.</summary>
/// <param name="Found">Whether the document exists.</param>
/// <param name="Id">The document id.</param>
/// <param name="Collection">The collection.</param>
/// <param name="Version">The document version.</param>
/// <param name="Body">The plain JSON body.</param>
public sealed record DocumentReadOutcome(bool Found, string? Id, string? Collection, ulong? Version, JsonElement? Body);

/// <summary>The write or delete outcome.</summary>
/// <param name="Outcome"><c>committed</c>, <c>conflict</c>, or <c>rejected</c>.</param>
/// <param name="Sequence">The commit receipt's journal sequence, when committed.</param>
/// <param name="Version">The commit receipt's document version, when committed.</param>
/// <param name="OperationId">The commit receipt's operation id, when committed.</param>
/// <param name="Reason">The rejection reason, when rejected.</param>
public sealed record WriteOutcome(string Outcome, ulong? Sequence, ulong? Version, string? OperationId, string? Reason);

/// <summary>The TQL query outcome.</summary>
/// <param name="Documents">The matched documents (id, collection, version, plain JSON body).</param>
/// <param name="Count">The matched count.</param>
/// <param name="Terminal">The terminal state of the query.</param>
public sealed record QueryOutcome(IReadOnlyList<DocumentReadOutcome> Documents, uint Count, string Terminal);

/// <summary>The sync-session diagnostics: the host's tracked registry, or the engine's counters on a direct channel.</summary>
/// <param name="Available">Whether the session registry is available on this surface.</param>
/// <param name="Sessions">The registry's live Sync Sessions, when available.</param>
/// <param name="EngineSessions">The engine's session counter, when read directly.</param>
public sealed record SessionsReport(bool Available, IReadOnlyList<(ulong SessionId, string Collection, int Generation)>? Sessions, uint? EngineSessions);

/// <summary>The graceful-shutdown answer.</summary>
/// <param name="State">The engine's state after the request.</param>
/// <param name="InFlight">The in-flight frames the engine is draining.</param>
public sealed record ShutdownReport(string State, uint InFlight);

/// <summary>The typed refusal of a surface: the host's or engine's bounded error code and detail.</summary>
/// <param name="Code">The closed, snake_case error code.</param>
/// <param name="Detail">The bounded machine-readable detail.</param>
public sealed class SurfaceRefusalException(string code, string detail) : Exception($"{code}: {detail}")
{
    /// <summary>The closed error code.</summary>
    public string Code { get; } = code;

    /// <summary>The bounded detail.</summary>
    public string Detail { get; } = detail;
}

/// <summary>The typed unavailability of a surface: the endpoint cannot be reached or is not serving.</summary>
/// <param name="detail">The bounded machine-readable detail.</param>
public sealed class SurfaceUnavailableException(string detail) : Exception(detail);
