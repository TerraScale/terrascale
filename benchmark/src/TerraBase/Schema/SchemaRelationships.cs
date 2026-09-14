using System.Collections.Concurrent;

namespace TerraBase.Schema;

/// <summary>A stable edge between two document keys.</summary>
public sealed record RelationshipEdge(string Id, string Relationship, DocumentKey Source, DocumentKey Target)
{
    /// <summary>Creates an edge with a validated identity.</summary>
    public RelationshipEdge(string id, string relationship, DocumentKey source, DocumentKey target) : this()
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("An edge identifier is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(relationship))
        {
            throw new ArgumentException("A relationship name is required.", nameof(relationship));
        }

        if (source.Tenant != target.Tenant)
        {
            throw new ArgumentException("Relationship endpoints must share a tenant.", nameof(target));
        }

        Id = id;
        Relationship = relationship;
        Source = source;
        Target = target;
    }
}

/// <summary>The effect planned for one endpoint deletion.</summary>
public enum RelationshipDeleteAction
{
    /// <summary>Leave the edge in place.</summary>
    Preserve,

    /// <summary>Clear the dependent endpoint relationship.</summary>
    Nullify,

    /// <summary>Delete the dependent endpoint.</summary>
    Cascade,

    /// <summary>Reject the endpoint deletion.</summary>
    Restrict,
}

/// <summary>One relationship edge and the policy that governs its deletion.</summary>
public sealed record RelationshipDeletePlan(RelationshipEdge Edge, RelationshipDeleteAction Action);

/// <summary>A bounded in-memory forward and reverse relationship index.</summary>
public sealed class RelationshipIndex
{
    private readonly object gate = new();
    private readonly RelationshipMetadata metadata;
    private readonly Dictionary<string, RelationshipEdge> edges = new(StringComparer.Ordinal);
    private readonly Dictionary<DocumentKey, SortedSet<string>> forward = [];
    private readonly Dictionary<DocumentKey, SortedSet<string>> reverse = [];

    /// <summary>Creates an index for one declared relationship.</summary>
    public RelationshipIndex(RelationshipMetadata metadata)
    {
        this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    /// <summary>The relationship metadata used by this index.</summary>
    public RelationshipMetadata Metadata => metadata;

    /// <summary>Adds one edge and updates both directions atomically.</summary>
    public void Add(RelationshipEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (!string.Equals(edge.Relationship, metadata.Name, StringComparison.Ordinal)
            || !string.Equals(edge.Source.Collection.Value, metadata.SourceCollection, StringComparison.Ordinal)
            || !string.Equals(edge.Target.Collection.Value, metadata.TargetCollection, StringComparison.Ordinal))
        {
            throw new ArgumentException("The edge does not match the relationship metadata.", nameof(edge));
        }

        lock (gate)
        {
            if (edges.TryGetValue(edge.Id, out var existing))
            {
                if (existing != edge)
                {
                    throw new InvalidOperationException("The edge identifier is already bound to another edge.");
                }

                return;
            }

            var sourceEdges = GetIds(forward, edge.Source);
            var targetEdges = GetIds(reverse, edge.Target);
            if (metadata.Kind is RelationshipKind.OneToOne && (sourceEdges.Count > 0 || targetEdges.Count > 0)
                || metadata.Kind is RelationshipKind.OneToMany && targetEdges.Count > 0
                || metadata.Kind is RelationshipKind.ManyToOne && sourceEdges.Count > 0)
            {
                throw new InvalidOperationException("The edge violates relationship cardinality.");
            }

            edges.Add(edge.Id, edge);
            sourceEdges.Add(edge.Id);
            targetEdges.Add(edge.Id);
        }
    }

    /// <summary>Removes one edge and updates both directions atomically.</summary>
    public bool Remove(string edgeId)
    {
        if (string.IsNullOrWhiteSpace(edgeId))
        {
            return false;
        }

        lock (gate)
        {
            if (!edges.Remove(edgeId, out var edge))
            {
                return false;
            }

            RemoveId(forward, edge.Source, edge.Id);
            RemoveId(reverse, edge.Target, edge.Id);
            return true;
        }
    }

    /// <summary>Returns edges leaving an endpoint in stable edge order.</summary>
    public IReadOnlyList<RelationshipEdge> Forward(DocumentKey source)
    {
        lock (gate)
        {
            return GetIds(forward, source).Select(id => edges[id]).ToArray();
        }
    }

    /// <summary>Returns edges entering an endpoint in stable edge order.</summary>
    public IReadOnlyList<RelationshipEdge> Reverse(DocumentKey target)
    {
        lock (gate)
        {
            return GetIds(reverse, target).Select(id => edges[id]).ToArray();
        }
    }

    /// <summary>Plans the declared delete behavior for an endpoint.</summary>
    public IReadOnlyList<RelationshipDeletePlan> PlanDelete(DocumentKey endpoint)
    {
        lock (gate)
        {
            var affected = new List<RelationshipDeletePlan>();
            foreach (var edge in GetIds(forward, endpoint).Concat(GetIds(reverse, endpoint)).Distinct(StringComparer.Ordinal).Select(id => edges[id]))
            {
                affected.Add(new RelationshipDeletePlan(edge, ToAction(metadata.OnDelete)));
            }

            return affected;
        }
    }

    private static RelationshipDeleteAction ToAction(DeletePolicy policy) => policy switch
    {
        DeletePolicy.Preserve => RelationshipDeleteAction.Preserve,
        DeletePolicy.Nullify => RelationshipDeleteAction.Nullify,
        DeletePolicy.Cascade => RelationshipDeleteAction.Cascade,
        DeletePolicy.Restrict => RelationshipDeleteAction.Restrict,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    private static SortedSet<string> GetIds(Dictionary<DocumentKey, SortedSet<string>> index, DocumentKey key)
    {
        if (!index.TryGetValue(key, out var ids))
        {
            ids = new SortedSet<string>(StringComparer.Ordinal);
            index.Add(key, ids);
        }

        return ids;
    }

    private static void RemoveId(Dictionary<DocumentKey, SortedSet<string>> index, DocumentKey key, string edgeId)
    {
        if (index.TryGetValue(key, out var ids))
        {
            ids.Remove(edgeId);
            if (ids.Count == 0)
            {
                index.Remove(key);
            }
        }
    }
}

/// <summary>Registry of versioned schemas and their migration chains.</summary>
public sealed class SchemaRegistry
{
    private readonly ConcurrentDictionary<string, SchemaMetadata> schemas = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SchemaMigrationSet> migrations = new(StringComparer.Ordinal);

    /// <summary>Registers metadata idempotently by collection name.</summary>
    public void Register(SchemaMetadata schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        schemas.AddOrUpdate(schema.Name, schema, static (_, current, incoming) =>
        {
            if (current.Version == incoming.Version && string.Equals(current.Fingerprint, incoming.Fingerprint, StringComparison.Ordinal))
            {
                return current;
            }

            if (incoming.Version <= current.Version)
            {
                throw new InvalidOperationException("A schema version must increase or repeat the exact same metadata.");
            }

            return incoming;
        }, schema);
    }

    /// <summary>Registers metadata under an explicit collection name.</summary>
    public void Register(string collection, SchemaMetadata schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(schema);
        if (!string.Equals(collection, schema.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException("The collection and schema names must match.", nameof(collection));
        }

        Register(schema);
    }

    /// <summary>Looks up the current metadata for a collection.</summary>
    public SchemaMetadata Get(string collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        return schemas.TryGetValue(collection, out var schema)
            ? schema
            : throw new KeyNotFoundException($"No schema is registered for '{collection}'.");
    }

    /// <summary>Returns whether metadata is registered for a collection.</summary>
    public bool TryGet(string collection, out SchemaMetadata? schema) => schemas.TryGetValue(collection, out schema);

    /// <summary>Validates one JSON document against the current collection schema.</summary>
    public SchemaValidationResult Validate(string collection, System.Text.Json.JsonElement document) => Get(collection).Validate(document);

    /// <summary>Registers a migration in one collection's chain.</summary>
    public void RegisterMigration(string collection, SchemaMigration migration)
    {
        var schema = Get(collection);
        ArgumentNullException.ThrowIfNull(migration);
        if (migration.FromVersion >= schema.Version)
        {
            throw new InvalidOperationException("A migration must start below the current schema version.");
        }

        migrations.GetOrAdd(collection, static _ => new SchemaMigrationSet()).Add(migration);
    }

    /// <summary>Applies a collection's contiguous migration chain.</summary>
    public SchemaMigrationResult Migrate(string collection, System.Text.Json.JsonElement document, ulong fromVersion, ulong toVersion)
    {
        _ = Get(collection);
        return migrations.TryGetValue(collection, out var set)
            ? set.Apply(document, fromVersion, toVersion)
            : throw new InvalidOperationException("No migrations are registered for this collection.");
    }

    /// <summary>Returns all forward relationships registered in a collection.</summary>
    public IReadOnlyList<RelationshipMetadata> Relationships(string collection) => Get(collection).Relationships;

    /// <summary>Returns generated reverse relationship metadata for a collection.</summary>
    public IReadOnlyList<RelationshipMetadata> ReverseRelationships(string collection) => Get(collection).ReverseRelationships;
}
