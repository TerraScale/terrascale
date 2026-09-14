using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraBase.Reactive;

/// <summary>Identifies a registered query or mutation operation.</summary>
public readonly record struct ReactiveOperationId
{
    /// <summary>Creates an operation identity.</summary>
    public ReactiveOperationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An operation identity is required.", nameof(value));
        }

        Value = value.Trim();
    }

    /// <summary>The stable operation name.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies the authorization scope under which a query is evaluated.</summary>
public sealed record ReactiveAuthorization
{
    /// <summary>Creates an authorization scope.</summary>
    public ReactiveAuthorization(
        TenantId tenant,
        string subject,
        IReadOnlyDictionary<string, string>? claims = null,
        long generation = 0)
    {
        tenant.EnsureValid(nameof(tenant));
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("An authorization subject is required.", nameof(subject));
        }

        Tenant = tenant;
        Subject = subject.Trim();
        Claims = claims is null
            ? ImmutableDictionary<string, string>.Empty
            : claims.ToImmutableDictionary(StringComparer.Ordinal);
        Generation = generation >= 0
            ? generation
            : throw new ArgumentOutOfRangeException(nameof(generation));
    }

    /// <summary>The tenant allowed to read or mutate data.</summary>
    public TenantId Tenant { get; }

    /// <summary>The authenticated principal.</summary>
    public string Subject { get; }

    /// <summary>Stable claims used by policy evaluation and cache identity.</summary>
    public IReadOnlyDictionary<string, string> Claims { get; }

    /// <summary>Authorization policy generation.</summary>
    public long Generation { get; }

    internal string ScopeKey()
    {
        var builder = new StringBuilder(Tenant.Value.Length + Subject.Length + Claims.Count * 16 + 32);
        builder.Append(Tenant.Value).Append('\u001f').Append(Subject).Append('\u001f').Append(Generation);
        foreach (var claim in Claims.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append('\u001f').Append(claim.Key).Append('=').Append(claim.Value);
        }

        return builder.ToString();
    }
}

/// <summary>One collection or document dependency recorded by a typed query.</summary>
public readonly record struct ReactiveDependency
{
    private ReactiveDependency(TenantId tenant, CollectionId collection, string? documentId, string? field)
    {
        Tenant = tenant;
        Collection = collection;
        DocumentId = documentId;
        Field = field;
    }

    /// <summary>Tracks a whole collection.</summary>
    public static ReactiveDependency Collection(TenantId tenant, CollectionId collection) =>
        new(tenant, collection, null, null);

    /// <summary>Tracks a field in every document in a collection.</summary>
    public static ReactiveDependency Field(TenantId tenant, CollectionId collection, string field) =>
        new(tenant, collection, null, ValidateField(field));

    /// <summary>Tracks one document.</summary>
    public static ReactiveDependency Document(DocumentKey key) =>
        new(key.Tenant, key.Collection, key.DocumentId, null);

    /// <summary>The dependency tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>The dependency collection.</summary>
    public CollectionId Collection { get; }

    /// <summary>The tracked document, or <see langword="null"/> for a collection dependency.</summary>
    public string? DocumentId { get; }

    /// <summary>The tracked field, or <see langword="null"/> for a row dependency.</summary>
    public string? Field { get; }

    internal bool Matches(ReactiveChange change)
    {
        if (Tenant != change.Key.Tenant || Collection != change.Key.Collection)
        {
            return false;
        }

        if (DocumentId is not null && !string.Equals(DocumentId, change.Key.DocumentId, StringComparison.Ordinal))
        {
            return false;
        }

        return Field is null
            || change.ChangedFields.Count == 0
            || change.ChangedFields.Contains(Field, StringComparer.Ordinal);
    }

    private static string ValidateField(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new ArgumentException("A dependency field is required.", nameof(field));
        }

        return field.Trim();
    }
}

/// <summary>An immutable set of dependencies discovered by a query execution.</summary>
public sealed class ReactiveDependencies : IReadOnlyCollection<ReactiveDependency>
{
    private readonly ImmutableHashSet<ReactiveDependency> values;

    /// <summary>Creates an empty dependency set.</summary>
    public ReactiveDependencies()
        : this(ImmutableHashSet<ReactiveDependency>.Empty)
    {
    }

    private ReactiveDependencies(ImmutableHashSet<ReactiveDependency> values) => this.values = values;

    /// <summary>The number of tracked dependencies.</summary>
    public int Count => values.Count;

    /// <summary>Returns whether a publication affects this dependency set.</summary>
    public bool IsAffectedBy(IEnumerable<ReactiveChange> changes) => changes.Any(change => values.Any(dependency => dependency.Matches(change)));

    /// <summary>Returns a set containing one additional dependency.</summary>
    public ReactiveDependencies Add(ReactiveDependency dependency) => new(values.Add(dependency));

    /// <inheritdoc />
    public IEnumerator<ReactiveDependency> GetEnumerator() => values.GetEnumerator();

    /// <inheritdoc />
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One committed document change that may invalidate a live query.</summary>
public sealed record ReactiveChange(
    DocumentKey Key,
    WriteOutcome Outcome,
    ulong Version,
    IReadOnlySet<string> ChangedFields,
    Document? Before = null,
    Document? After = null)
{
    /// <summary>Creates a change with no field-level information, invalidating row dependencies.</summary>
    public static ReactiveChange ForDocument(
        DocumentKey key,
        WriteOutcome outcome,
        ulong version,
        Document? before = null,
        Document? after = null) =>
        new(key, outcome, version, ImmutableHashSet<string>.Empty, before, after);

    /// <summary>Creates a field-scoped change.</summary>
    public static ReactiveChange ForFields(
        DocumentKey key,
        WriteOutcome outcome,
        ulong version,
        IEnumerable<string> changedFields,
        Document? before = null,
        Document? after = null)
    {
        ArgumentNullException.ThrowIfNull(changedFields);
        var fields = changedFields
            .Select(field => string.IsNullOrWhiteSpace(field)
                ? throw new ArgumentException("Changed field names cannot be empty.", nameof(changedFields))
                : field.Trim())
            .ToImmutableHashSet(StringComparer.Ordinal);
        return new(key, outcome, version, fields, before, after);
    }
}

/// <summary>An ordered committed publication used for resumable subscriptions.</summary>
public sealed record ReactiveChangeSet(long Sequence, IReadOnlyList<ReactiveChange> Changes)
{
    /// <summary>Creates an ordered publication.</summary>
    public ReactiveChangeSet(long sequence, IEnumerable<ReactiveChange> changes)
        : this(sequence, changes?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(changes)))
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (Changes.Count == 0)
        {
            throw new ArgumentException("A publication must contain at least one change.", nameof(changes));
        }
    }
}

/// <summary>Opaque resumable position bound to one subscription identity.</summary>
public readonly record struct ReactiveCheckpoint
{
    internal ReactiveCheckpoint(string subscriptionKey, long sequence)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(subscriptionKey));
        SubscriptionDigest = Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
        Sequence = sequence;
    }

    /// <summary>The last applied publication sequence.</summary>
    public long Sequence { get; }

    /// <summary>A digest binding the checkpoint to its query and authorization scope.</summary>
    public string SubscriptionDigest { get; }

    /// <summary>Serializes the opaque checkpoint.</summary>
    public string Encode() => $"tsr1.{SubscriptionDigest}.{Sequence}";

    internal static bool TryDecode(string value, string subscriptionKey, out ReactiveCheckpoint checkpoint)
    {
        checkpoint = default;
        var fields = value.Split('.', StringSplitOptions.None);
        if (fields.Length != 3 || fields[0] != "tsr1" || !long.TryParse(fields[2], out var sequence) || sequence < 0)
        {
            return false;
        }

        var expected = new ReactiveCheckpoint(subscriptionKey, sequence);
        if (!string.Equals(expected.SubscriptionDigest, fields[1], StringComparison.Ordinal))
        {
            return false;
        }

        checkpoint = expected;
        return true;
    }
}

/// <summary>The kind of item delivered by a reactive subscription.</summary>
public enum ReactiveFrameKind
{
    /// <summary>The first complete materialized result.</summary>
    Snapshot,

    /// <summary>A result revision after an affected publication.</summary>
    Delta,

    /// <summary>The retained log no longer covers a requested checkpoint.</summary>
    Reset,

    /// <summary>The consumer must replace local state because its queue overflowed.</summary>
    Backpressure,

    /// <summary>Authorization no longer permits the subscription.</summary>
    AuthorizationRevoked,
}

/// <summary>A complete result revision or control frame.</summary>
public sealed record ReactiveFrame<TResult>(
    ReactiveFrameKind Kind,
    long Sequence,
    string Checkpoint,
    TResult? Result,
    IReadOnlyList<ReactiveChange> Changes,
    long? DroppedThrough = null,
    string? Reason = null)
{
    /// <summary>Creates a complete snapshot frame.</summary>
    public static ReactiveFrame<TResult> Snapshot(long sequence, string checkpoint, TResult result) =>
        new(ReactiveFrameKind.Snapshot, sequence, checkpoint, result, Array.Empty<ReactiveChange>());

    /// <summary>Creates a result delta frame.</summary>
    public static ReactiveFrame<TResult> Delta(long sequence, string checkpoint, TResult result, IReadOnlyList<ReactiveChange> changes) =>
        new(ReactiveFrameKind.Delta, sequence, checkpoint, result, changes);
}

/// <summary>Execution context supplied to a typed query.</summary>
public sealed class ReactiveQueryContext
{
    private readonly IDocumentStore? documents;
    private ReactiveDependencies dependencies = new();

    internal ReactiveQueryContext(ReactiveAuthorization authorization, IDocumentStore? documents)
    {
        Authorization = authorization;
        this.documents = documents;
    }

    /// <summary>The authenticated scope used by this execution.</summary>
    public ReactiveAuthorization Authorization { get; }

    /// <summary>The dependencies discovered during this execution.</summary>
    public ReactiveDependencies Dependencies => dependencies;

    internal void ReplaceDependencies(ReactiveDependencies value) => dependencies = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>Tracks a collection dependency.</summary>
    public void Track(TenantId tenant, CollectionId collection) => dependencies = dependencies.Add(ReactiveDependency.Collection(tenant, collection));

    /// <summary>Tracks a field dependency.</summary>
    public void Track(TenantId tenant, CollectionId collection, string field) => dependencies = dependencies.Add(ReactiveDependency.Field(tenant, collection, field));

    /// <summary>Tracks one document dependency.</summary>
    public void Track(DocumentKey key) => dependencies = dependencies.Add(ReactiveDependency.Document(key));

    /// <summary>Reads through the configured document store and records a document dependency.</summary>
    public async ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        Track(key);
        return documents is null
            ? throw new InvalidOperationException("This query context has no document source.")
            : await documents.ReadAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scans through the configured document store and records a collection dependency.</summary>
    public IAsyncEnumerable<Document> ScanAsync(TenantId tenant, CollectionId collection, CancellationToken cancellationToken = default)
    {
        Track(tenant, collection);
        return documents is null
            ? throw new InvalidOperationException("This query context has no document source.")
            : documents.ScanAsync(tenant, collection, cancellationToken);
    }
}

/// <summary>Typed read operation executed by the reactive runtime.</summary>
public sealed class ReactiveQuery<TParameters, TResult>
{
    private readonly Func<TParameters, ReactiveQueryContext, CancellationToken, ValueTask<TResult>> handler;

    /// <summary>Creates a typed query.</summary>
    public ReactiveQuery(
        ReactiveOperationId id,
        Func<TParameters, ReactiveQueryContext, CancellationToken, ValueTask<TResult>> handler,
        Func<TParameters, ReactiveDependencies>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Id = id;
        this.handler = handler;
        DependencySelector = dependencies;
    }

    /// <summary>The operation identity.</summary>
    public ReactiveOperationId Id { get; }

    /// <summary>An optional static dependency declaration.</summary>
    public Func<TParameters, ReactiveDependencies>? DependencySelector { get; }

    internal ValueTask<TResult> ExecuteAsync(TParameters parameters, ReactiveQueryContext context, CancellationToken cancellationToken) =>
        handler(parameters, context, cancellationToken);
}

/// <summary>Typed mutation operation. The handler runs inside one transaction context.</summary>
public sealed class ReactiveMutation<TInput, TResult>
{
    private readonly Func<TInput, ReactiveMutationContext, CancellationToken, ValueTask<TResult>> handler;

    /// <summary>Creates a typed mutation.</summary>
    public ReactiveMutation(
        ReactiveOperationId id,
        Func<TInput, ReactiveMutationContext, CancellationToken, ValueTask<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Id = id;
        this.handler = handler;
    }

    /// <summary>The operation identity.</summary>
    public ReactiveOperationId Id { get; }

    internal ValueTask<TResult> ExecuteAsync(TInput input, ReactiveMutationContext context, CancellationToken cancellationToken) =>
        handler(input, context, cancellationToken);
}

/// <summary>Policy used for a full subscriber queue.</summary>
public enum ReactiveBackpressureMode
{
    /// <summary>Drop queued items and force a complete replacement snapshot.</summary>
    Reset,

    /// <summary>Close the subscriber with a typed backpressure error.</summary>
    Disconnect,
}

/// <summary>Subscription delivery and retention limits.</summary>
public sealed record ReactiveOptions
{
    /// <summary>Maximum queued frames for one subscriber.</summary>
    public int SubscriberBufferSize { get; init; } = 32;

    /// <summary>Maximum retained result revisions for one shared query.</summary>
    public int RetainedRevisions { get; init; } = 256;

    /// <summary>Behavior when a subscriber cannot keep up.</summary>
    public ReactiveBackpressureMode Backpressure { get; init; } = ReactiveBackpressureMode.Reset;

    internal void Validate()
    {
        if (SubscriberBufferSize is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(SubscriberBufferSize));
        }

        if (RetainedRevisions is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(RetainedRevisions));
        }
    }
}

/// <summary>Authorization result used when admitting or refreshing a subscription.</summary>
public readonly record struct ReactiveAuthorizationDecision(bool Allowed, string? Reason = null)
{
    /// <summary>Allows access.</summary>
    public static ReactiveAuthorizationDecision Allow { get; } = new(true);

    /// <summary>Denies access.</summary>
    public static ReactiveAuthorizationDecision Deny(string reason) => new(false, reason);
}

/// <summary>Evaluates authorization at attach and authorization-generation changes.</summary>
public interface IReactiveAuthorizer
{
    /// <summary>Checks access to one operation under one scope.</summary>
    ValueTask<ReactiveAuthorizationDecision> AuthorizeQueryAsync(
        ReactiveOperationId operation,
        ReactiveAuthorization authorization,
        CancellationToken cancellationToken = default);
}

/// <summary>Default authorizer allowing already authenticated callers.</summary>
public sealed class AllowAuthenticatedReactiveAuthorizer : IReactiveAuthorizer
{
    /// <inheritdoc />
    public ValueTask<ReactiveAuthorizationDecision> AuthorizeQueryAsync(
        ReactiveOperationId operation,
        ReactiveAuthorization authorization,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(ReactiveAuthorizationDecision.Allow);
}
