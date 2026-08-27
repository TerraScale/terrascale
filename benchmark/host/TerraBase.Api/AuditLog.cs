// <copyright file="AuditLog.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>
/// The lifecycle audit log of the API host (SE-08): a bounded ring of
/// typed lifecycle records. The discipline is enforced by construction
/// (PD-2026-08-26-11 / the SE-08 record): a record carries only
/// enumerable lifecycle facts — the principal and tenant names the
/// backend supplied, the operation name, the outcome, and a bounded
/// machine-readable detail — never a document body, never a payload,
/// never a credential or header value.
/// </summary>
public sealed class AuditLog
{
    /// <summary>The retained-record bound of the ring.</summary>
    public const int Capacity = 1024;

    /// <summary>The byte bound of the detail field.</summary>
    public const int MaxDetailLength = 256;

    private readonly object gate = new();
    private readonly Queue<AuditRecord> records = new(Capacity);
    private long sequence;

    /// <summary>Records one lifecycle fact.</summary>
    /// <param name="kind">The enumerable lifecycle kind.</param>
    /// <param name="tenant">The backend-supplied tenant, when a caller was identified.</param>
    /// <param name="principal">The backend-supplied principal, when a caller was identified.</param>
    /// <param name="operation">The operation or endpoint name.</param>
    /// <param name="outcome">The enumerable outcome.</param>
    /// <param name="detail">A bounded machine-readable detail; never a body or credential.</param>
    public void Record(
        AuditKind kind,
        string? tenant,
        string? principal,
        string operation,
        AuditOutcome outcome,
        string detail = "")
    {
        var record = new AuditRecord(
            Sequence: Interlocked.Increment(ref this.sequence),
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind: kind,
            Tenant: tenant,
            Principal: principal,
            Operation: operation,
            Outcome: outcome,
            Detail: detail.Length > MaxDetailLength ? detail[..MaxDetailLength] : detail);
        lock (this.gate)
        {
            this.records.Enqueue(record);
            while (this.records.Count > Capacity)
            {
                this.records.Dequeue();
            }
        }
    }

    /// <summary>Reads the most recent records, newest first.</summary>
    /// <param name="limit">The maximum record count; the ring bound caps it.</param>
    /// <returns>The records, newest first.</returns>
    public IReadOnlyList<AuditRecord> Recent(int limit)
    {
        lock (this.gate)
        {
            var bounded = Math.Clamp(limit, 0, Capacity);
            return this.records.Reverse().Take(bounded).ToArray();
        }
    }

    /// <summary>The retained record count.</summary>
    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.records.Count;
            }
        }
    }
}

/// <summary>The enumerable lifecycle kinds of the host's audit record.</summary>
public enum AuditKind
{
    /// <summary>The local gate refused a caller without backend-supplied context.</summary>
    GateRefused,

    /// <summary>The host refused a request whose scope did not validate.</summary>
    ScopeRefused,

    /// <summary>The engine refused a cross-tenant lifecycle frame (engine-enforced isolation).</summary>
    TenantRefused,

    /// <summary>A tenant channel was bound to the engine.</summary>
    ChannelBound,

    /// <summary>A sync session was opened on the engine.</summary>
    SessionOpened,

    /// <summary>A sync session was closed.</summary>
    SessionClosed,

    /// <summary>A sync session ended with an engine restart (no orphaned state).</summary>
    SessionEndedByRestart,

    /// <summary>A server-ack mutation was applied or deduplicated.</summary>
    MutationApplied,

    /// <summary>A server-ack mutation was rejected by the engine.</summary>
    MutationRejected,

    /// <summary>A backbone stream subscription was opened.</summary>
    SubscriptionOpened,

    /// <summary>A backbone stream subscription was closed.</summary>
    SubscriptionClosed,

    /// <summary>A stream queue overflowed and dropped frames (the count is surfaced).</summary>
    StreamDropped,

    /// <summary>The engine process was lost.</summary>
    EngineLost,

    /// <summary>The engine process was started or restarted.</summary>
    EngineStarted,

    /// <summary>A graceful engine shutdown began.</summary>
    EngineShutdown,
}

/// <summary>The enumerable outcomes of the host's audit record.</summary>
public enum AuditOutcome
{
    /// <summary>The operation was accepted.</summary>
    Accepted,

    /// <summary>The operation was refused, typed.</summary>
    Refused,

    /// <summary>The operation failed on the engine channel.</summary>
    Failed,

    /// <summary>A lifecycle fact, not an operation outcome.</summary>
    Noted,
}

/// <summary>One lifecycle audit record: enumerable facts only, never a document body or credential.</summary>
/// <param name="Sequence">The record sequence.</param>
/// <param name="Timestamp">The Unix-epoch milliseconds.</param>
/// <param name="Kind">The lifecycle kind.</param>
/// <param name="Tenant">The backend-supplied tenant, when identified.</param>
/// <param name="Principal">The backend-supplied principal, when identified.</param>
/// <param name="Operation">The operation or endpoint name.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Detail">The bounded machine-readable detail.</param>
public sealed record AuditRecord(
    long Sequence,
    long Timestamp,
    AuditKind Kind,
    string? Tenant,
    string? Principal,
    string Operation,
    AuditOutcome Outcome,
    string Detail);
