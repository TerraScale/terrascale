using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Webhooks;

/// <summary>The HTTPS receiver to register.</summary>
/// <param name="Url">Public HTTPS receiver URL.</param>
public sealed record CreateWebhookRequest(string? Url);

/// <summary>A webhook, without its signing secret.</summary>
/// <param name="Id">Webhook ULID.</param>
/// <param name="Url">Receiver URL.</param>
/// <param name="CreatedAt">Registration time.</param>
/// <param name="Status">active or feed_gap.</param>
public sealed record WebhookBody(string Id, string Url, DateTimeOffset CreatedAt, string Status);

/// <summary>A registration response; the secret is never returned again.</summary>
/// <param name="Id">Webhook ULID.</param>
/// <param name="Url">Receiver URL.</param>
/// <param name="CreatedAt">Registration time.</param>
/// <param name="Status">active or feed_gap.</param>
/// <param name="Secret">Base64url encoded 32-byte HMAC key.</param>
public sealed record CreatedWebhookBody(string Id, string Url, DateTimeOffset CreatedAt, string Status, string Secret);

/// <summary>Delivery history without signed bodies or secrets.</summary>
/// <param name="Id">Stable delivery ULID.</param>
/// <param name="CreatedAt">Capture time.</param>
/// <param name="Status">pending or delivered.</param>
/// <param name="Attempts">Number of recorded attempts.</param>
/// <param name="LastAttemptAt">Last attempt time.</param>
/// <param name="NextAttemptAt">Next retry time, or null after success.</param>
/// <param name="HttpStatus">Receiver status, when received.</param>
/// <param name="ErrorCode">Sanitized failure category, or null.</param>
public sealed record WebhookDeliveryBody(string Id, DateTimeOffset CreatedAt, string Status, long Attempts,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt, int? HttpStatus, string? ErrorCode);

/// <summary>One document change identity; fetch current state using the document API.</summary>
/// <param name="Origin">Opaque source node.</param>
/// <param name="Version">Opaque commit stamp.</param>
/// <param name="Id">Document key.</param>
/// <param name="Deleted">Whether this operation deleted the document.</param>
public sealed record WebhookChange(string Origin, string Version, string Id, bool Deleted);

/// <summary>The signed receiver payload.</summary>
/// <param name="Id">Delivery ULID.</param>
/// <param name="WebhookId">Webhook ULID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Database">Database name.</param>
/// <param name="Collection">Collection name.</param>
/// <param name="Events">Bounded document change identities.</param>
public sealed record WebhookPayload(string Id, string WebhookId, string ProjectId, string Database, string Collection, WebhookChange[] Events);

/// <summary>A webhook operation or a public, sanitized failure.</summary>
public readonly union WebhookResult(CreatedWebhookBody, WebhookBody[], WebhookDeliveryBody[], Done, Validation, NotFound, Conflict, Unavailable);

/// <summary>Tenant-scoped webhook registration and delivery history.</summary>
public interface IWebhooks
{
    /// <summary>Registers from the local feed head; returns the signing key once.</summary>
    ValueTask<WebhookResult> CreateAsync(TenantContext tenant, string database, string collection, string? url, CancellationToken cancellationToken);

    /// <summary>Lists registrations, never keys.</summary>
    ValueTask<WebhookResult> ListAsync(TenantContext tenant, string collection, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Deletes the registration and delivery history.</summary>
    ValueTask<WebhookResult> DeleteAsync(TenantContext tenant, string collection, string id, CancellationToken cancellationToken);

    /// <summary>Lists sanitized delivery history.</summary>
    ValueTask<WebhookResult> DeliveriesAsync(TenantContext tenant, string collection, string id, string? after, int limit, CancellationToken cancellationToken);
}

/// <summary>Removes a permanently deleted database's webhook state from this node before its purge receipt.</summary>
public interface IWebhookErasure
{
    /// <summary>
    /// Erases this node's webhook state for a permanently deleted database and reports whether, at that point, none of the
    /// exact (organization, project, database) scope remains here: no registration, cursor, delivery body or history in its
    /// prefix, no dispatch of that scope, and no send in flight. Each call does bounded work and resumes its pass; false until
    /// a complete pass succeeds, while the permanent purge authority is missing, unreadable or names another scope, a dispatch
    /// names the database under another organization or project, the catalog is unavailable, or a cancelled send has not
    /// returned yet. Callers retry. Logical absence only, not physical erasure of earlier history.
    /// </summary>
    ValueTask<bool> EraseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this node's last successful <see cref="EraseAsync"/> still holds: no dispatch naming the database under another
    /// scope was admitted since. Call inside the receipt's write transaction: replicated admission counts a conflict under
    /// the same writer, so one committed before that transaction withholds the receipt. A receipt is point-in-time absence of
    /// the exact scope only; replication refuses that scope afterwards, but a later other-scope conflict is kept, not
    /// discarded, and is surfaced only by <c>terrabase.webhooks.replica_conflicts</c> and later false results here.
    /// </summary>
    bool Certifies(TenantContext database);
}

/// <summary>
/// Admits replicated system-database webhook state inside the receiving transaction, so a permanent database deletion
/// dominates every late copy of its webhooks: refused state is never joined, so no row or change history of it is written.
/// </summary>
public interface IWebhookReplicaAdmission
{
    /// <summary>Whether <paramref name="collection"/> holds webhook state this admission decides.</summary>
    bool Decides(string collection);

    /// <summary>
    /// False when a permanent purge visible in <paramref name="transaction"/> covers this state; the caller must then not
    /// join it. Call after every other change of the same transaction joined, so authority arriving with it counts.
    /// Under a valid authority every row of the database's own prefix is refused, as is a dispatch of its scope or with
    /// a malformed tenant. A dispatch holding the reserved scalar-wrapper field (<c>"\u0000"</c>) is refused under any
    /// authority or none. Payload-free deletions, other databases, unreadable authority, and a dispatch naming a different
    /// (organization, project) pair are admitted; the last blocks certification. Never throws on stored content.
    /// </summary>
    ValueTask<bool> AdmitAsync(IStorageTransaction transaction, string collection, string id, DocumentState state, CancellationToken cancellationToken);
}

internal sealed record WebhookRecord(int Format, string Id, TenantContext Tenant, string Database, string Collection, string CollectionId,
    string Incarnation, string Machine, string Url, string Secret, DateTimeOffset CreatedAt, string Status, string Cursor, string? Pending);
internal sealed record WebhookDispatch(string Id, TenantContext Tenant, string Collection, string Machine);
internal sealed record WebhookDeliveryRecord(string Id, WebhookDeliveryBody Details, byte[] Body);

// Registration is immutable: worker updates must never defeat a replicated deletion.
internal sealed record WebhookRegistration(int Format, string Id, TenantContext Tenant, string Database, string Collection, string CollectionId,
    string Incarnation, string Machine, string Url, string Secret, DateTimeOffset CreatedAt);
internal sealed record WebhookState(string Id, string Cursor, string Status, string? Pending);
