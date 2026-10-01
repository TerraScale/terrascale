using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Native;

/// <summary>Strict native v1 JSON and TB-DOC-EFFECT-v1 binary encoding; no legacy JSON document conversion.</summary>
public static class NativeCodec
{
    /// <summary>The maximum native wire request or response body size.</summary>
    public const int MaximumBodyBytes = 4 * 1024 * 1024;
    private const int MaximumValueBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Validates a canonical lowercase UUID without transforming it.</summary>
    public static string CanonicalUuid(string value, string name)
    {
        if (value is null || value.Length != 36 || value.Where((character, index) =>
                index is 8 or 13 or 18 or 23 ? character != '-' : !IsLowerHex(character)).Any())
            throw Invalid($"{name} must be a canonical lowercase UUID.");
        return value;
    }

    /// <summary>Validates provisioned native scope identifiers without inventing schema or grant bindings.</summary>
    public static NativeScope ValidateScope(NativeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CanonicalUuid(scope.Tenant, "tenant");
        CanonicalUuid(scope.Database, "database");
        return scope;
    }

    /// <summary>Builds the exact native v1 scope path from validated identifiers.</summary>
    public static string ScopePath(NativeScope scope)
    {
        ValidateScope(scope);
        return $"/v1/tenants/{scope.Tenant}/databases/{scope.Database}/shards/{Decimal(scope.Shard)}";
    }

    /// <summary>Validates a bounded opaque producer cursor without interpreting its contents.</summary>
    public static string Cursor(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw Invalid("Cursor must be bounded opaque base64url.");
        return value;
    }

    /// <summary>Parses bounded strict JSON and rejects duplicate members; dispose the returned document.</summary>
    public static JsonDocument Parse(string json)
    {
        if (json.Length > MaximumBodyBytes || Utf8.GetByteCount(json) > MaximumBodyBytes)
            throw Invalid("Native JSON exceeds its byte limit.");
        return Parse(Utf8.GetBytes(json));
    }

    /// <summary>Parses bounded strict JSON and rejects duplicate members; dispose the returned document.</summary>
    public static JsonDocument Parse(byte[] json) => ParseBounded(json, MaximumBodyBytes);

    internal static JsonDocument ParseLocalOperation(byte[] json) => ParseBounded(json, MaximumBodyBytes + 8192);

    private static JsonDocument ParseBounded(byte[] json, int maximum)
    {
        if (json.Length > maximum) throw Invalid("Native JSON exceeds its byte limit.");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        try
        {
            RejectDuplicateMembers(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void RejectDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Native JSON contains duplicate object members.");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateMembers(item);
        }
    }

    /// <summary>Validates an ordered typed group and computes its exact immutable native body and effect identity.</summary>
    public static (string Body, NativeTransactionIdentity Identity) EncodeCommit(NativeScope scope, NativeCommit commit)
    {
        var identity = CommitIdentity(scope, commit);
        var authority = WireAuthority(commit.Authority);
        var transaction = WireTransaction(commit.TransactionId);
        // Reject oversized aggregate effects before allocating base64 wire
        // strings for every write. The digest walk uses one bounded buffer.
        var writes = new List<object>(commit.Writes.Length);
        foreach (var write in commit.Writes)
        {
            writes.Add(WireWrite(write, commit.Authority));
        }
        return (Json(Object(("version", 1), ("transaction_id", transaction), ("authority", authority),
            ("effect_digest", identity.EffectDigest), ("writes", writes))), identity);
    }

    private static NativeTransactionIdentity CommitIdentity(NativeScope scope, NativeCommit commit)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(commit);
        _ = WireAuthority(commit.Authority);
        _ = WireTransaction(commit.TransactionId);
        Bounded(commit.Writes, 1024, 1);
        return new NativeTransactionIdentity(commit.TransactionId, commit.Authority,
            EffectDigest(scope, commit.Authority, commit.Writes));
    }

    /// <summary>Encodes status lookup for the original transaction identity.</summary>
    public static string EncodeStatus(NativeTransactionIdentity identity) => Json(Object(
        ("version", 1), ("transaction_id", WireTransaction(identity.TransactionId)),
        ("authority", WireAuthority(identity.Authority)), ("effect_digest", Hex(identity.EffectDigest, 32, "effect digest"))));

    /// <summary>Encodes every state, sibling, schema and causal context in a native Point.</summary>
    public static string EncodePoint(NativePoint point)
    {
        var body = Json(writer => WritePoint(writer, point));
        using var document = Parse(body);
        DecodePoint(document.RootElement);
        return body;
    }

    /// <summary>Encodes a complete native query page and opaque continuation cursor.</summary>
    public static string EncodeQueryPage(NativeQueryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Bounded(page.Rows, 128);
        string? previous = null;
        foreach (var row in page.Rows)
        {
            var document = CanonicalUuid(row.Document, "document");
            if (previous is not null && string.CompareOrdinal(previous, document) >= 0)
                throw Invalid("Query documents must be strictly increasing.");
            previous = document;
        }
        var body = Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("rows");
            foreach (var row in page.Rows)
            {
                writer.WriteStartObject();
                writer.WriteString("document", row.Document);
                writer.WritePropertyName("point");
                WritePoint(writer, row.Point);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (page.NextCursor is not null) writer.WriteString("next_cursor", Cursor(page.NextCursor));
            writer.WriteEndObject();
        });
        using var encoded = Parse(body);
        DecodeQueryPage(encoded.RootElement, 128);
        return body;
    }

    /// <summary>Encodes an identity-bound native commit receipt.</summary>
    public static string EncodeReceipt(NativeCommitReceipt receipt) => Json(WireReceipt(receipt));

    /// <summary>Encodes an authoritative status and its retained outcome.</summary>
    public static string EncodeTransactionStatus(NativeTransactionStatus status)
    {
        var root = IdentityWire(status.Identity);
        root["version"] = 1;
        root["state"] = status.State.ToString().ToLowerInvariant();
        root["outcome"] = status.State switch
        {
            NativeTransactionState.Committed when status.Receipt is not null => Object(("receipt", WireReceipt(status.Receipt))),
            NativeTransactionState.Rejected when status.Receipt is null => Object(("error", Object(("code", "precondition_failed")))),
            NativeTransactionState.Pending or NativeTransactionState.Unknown or NativeTransactionState.Expired when status.Receipt is null => null,
            _ => throw Invalid("Native status contradicts its outcome.")
        };
        var json = Json(root);
        using var document = Parse(json);
        DecodeStatus(document.RootElement);
        return json;
    }

    private static Dictionary<string, object?> IdentityWire(NativeTransactionIdentity identity) => Object(
        ("transaction_id", WireTransaction(identity.TransactionId)), ("authority", WireAuthority(identity.Authority)),
        ("effect_digest", Hex(identity.EffectDigest, 32, "effect digest")));

    private static object WireReceipt(NativeCommitReceipt receipt)
    {
        if (receipt.CommitSequence == 0) throw Invalid("Commit sequence must be positive.");
        var root = IdentityWire(receipt.Identity);
        root["version"] = 1;
        root["durability"] = receipt.Durability switch
        {
            NativeDurability.LocalDurable => "local_durable",
            NativeDurability.ObjectDurable => "object_durable",
            _ => throw Invalid("Unsupported native durability.")
        };
        root["event"] = WireEvent(receipt.Event);
        root["commit_sequence"] = Decimal(receipt.CommitSequence);
        return root;
    }

    private static object WireEvent(NativeEvent occurrence)
    {
        if (occurrence.Sequence == 0) throw Invalid("Event sequence must be positive.");
        return Object(("database", CanonicalUuid(occurrence.Database, "database")), ("shard", Decimal(occurrence.Shard)),
            ("replica", CanonicalUuid(occurrence.Replica, "replica")), ("incarnation", Hex(occurrence.Incarnation, 16, "incarnation")),
            ("sequence", Decimal(occurrence.Sequence)));
    }

    private static void WritePoint(Utf8JsonWriter writer, NativePoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        Bounded(point.Context, int.MaxValue);
        Bounded(point.Siblings, int.MaxValue);
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteString("state", point.State.ToString().ToLowerInvariant());
        writer.WriteString("state_version", point.StateVersion);
        writer.WriteStartArray("context");
        string? previous = null;
        foreach (var entry in point.Context)
        {
            var order = CanonicalUuid(entry.Replica, "replica") + Hex(entry.Incarnation, 16, "incarnation");
            if (entry.Sequence == 0 || previous is not null && string.CompareOrdinal(previous, order) >= 0)
                throw Invalid("Observed origins must be strictly increasing with positive sequence.");
            previous = order;
            writer.WriteStartObject();
            writer.WriteString("replica", entry.Replica);
            writer.WriteString("incarnation", entry.Incarnation);
            writer.WriteString("sequence", Decimal(entry.Sequence));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("siblings");
        foreach (var sibling in point.Siblings)
        {
            var fields = Object(("event", WireEvent(sibling.Event)), ("schema_generation", Decimal(sibling.SchemaGeneration)),
                ("schema_digest", Hex(sibling.SchemaDigest, 32, "schema digest")));
            if (sibling.Value is NativeWriteValue.Live live)
            {
                ValidateDocument(live.Fields);
                fields["kind"] = "live";
                fields["fields"] = WireFields(live.Fields, 1, new Budget());
            }
            else if (sibling.Value is NativeWriteValue.Tombstone) fields["kind"] = "tombstone";
            else throw Invalid("Unsupported native sibling.");
            JsonSerializer.Serialize(writer, fields);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Validates and encodes a typed native query with a fixed page limit.</summary>
    public static string EncodeQuery(NativeQueryPredicate predicate, int limit, string? cursor = null)
    {
        if (limit is < 1 or > 128) throw Invalid("Query limit must be between 1 and 128.");
        Bounded(predicate.Path, 16, 1);
        foreach (var id in predicate.Path) FieldId(id);
        var operation = predicate.Operator switch
        {
            NativeQueryOperator.Equal => "equal",
            NativeQueryOperator.LessThan => "less_than",
            NativeQueryOperator.LessThanOrEqual => "less_than_or_equal",
            NativeQueryOperator.GreaterThan => "greater_than",
            NativeQueryOperator.GreaterThanOrEqual => "greater_than_or_equal",
            NativeQueryOperator.IsNull => "is_null",
            NativeQueryOperator.IsMissing => "is_missing",
            _ => throw Invalid("Unsupported query operator.")
        };
        var wirePredicate = Object(("op", operation), ("path", predicate.Path));
        if (predicate.Operator is NativeQueryOperator.IsNull or NativeQueryOperator.IsMissing)
        {
            if (predicate.Value is not null) throw Invalid("Presence predicates have no value.");
        }
        else
        {
            if (predicate.Value is not (NativeValue.Boolean or NativeValue.Integer or NativeValue.Text or NativeValue.Bytes))
                throw Invalid("Query values must be native scalars.");
            wirePredicate["value"] = WireValue(predicate.Value, 1, new Budget());
        }
        var request = Object(("version", 1), ("predicate", wirePredicate), ("limit", limit));
        if (cursor is not null) request["cursor"] = Cursor(cursor);
        return Json(request);
    }

    /// <summary>Strictly decodes a complete native Point without dropping conflict siblings.</summary>
    public static NativePoint DecodePoint(JsonElement root)
    {
        Fields(root, ["version", "state", "state_version", "context", "siblings"]);
        Version(root);
        var state = Text(root.GetProperty("state")) switch
        {
            "absent" => NativePointState.Absent,
            "deleted" => NativePointState.Deleted,
            "value" => NativePointState.Value,
            "conflict" => NativePointState.Conflict,
            _ => throw Invalid("Unsupported point state.")
        };
        var context = DecodeObserved(root.GetProperty("context"), int.MaxValue);
        var siblings = ImmutableArray.CreateBuilder<NativeSibling>();
        foreach (var raw in Array(root.GetProperty("siblings"), int.MaxValue))
        {
            Fields(raw, ["event", "schema_generation", "schema_digest", "kind"], ["fields"]);
            var value = DecodeWriteValue(raw, sibling: true);
            siblings.Add(new NativeSibling(DecodeEvent(raw.GetProperty("event")),
                U64(raw.GetProperty("schema_generation"), positive: true),
                Hex(Text(raw.GetProperty("schema_digest")), 32, "schema digest"), value));
        }
        var version = root.GetProperty("state_version").ValueKind == JsonValueKind.Null
            ? null : Hex(Text(root.GetProperty("state_version")), 32, "state version");
        var valid = state switch
        {
            NativePointState.Absent => context.Length == 0 && siblings.Count == 0 && version is null,
            NativePointState.Deleted => siblings.Count == 1 && siblings[0].Value is NativeWriteValue.Tombstone && version is not null,
            NativePointState.Value => siblings.Count == 1 && siblings[0].Value is NativeWriteValue.Live && version is not null,
            NativePointState.Conflict => siblings.Count >= 2 && version is null,
            _ => false
        };
        if (!valid) throw Invalid("Point state contradicts its siblings or condition token.");
        var observed = context.ToDictionary(origin => (origin.Replica, origin.Incarnation), origin => origin.Sequence);
        var events = new HashSet<(string, string, ulong)>();
        foreach (var sibling in siblings)
        {
            var occurrence = sibling.Event;
            if (!events.Add((occurrence.Replica, occurrence.Incarnation, occurrence.Sequence)) ||
                (!observed.TryGetValue((occurrence.Replica, occurrence.Incarnation), out var sequence) || sequence < occurrence.Sequence))
                throw Invalid("Point causal context is incomplete or contains duplicate siblings.");
        }
        return new NativePoint(state, version, context, siblings.ToImmutable());
    }

    /// <summary>Validates every ordered row before returning a bounded native page.</summary>
    public static NativeQueryPage DecodeQueryPage(JsonElement root, int limit)
    {
        Fields(root, ["version", "rows"], ["next_cursor"]);
        Version(root);
        var rows = ImmutableArray.CreateBuilder<NativeQueryRow>();
        string? previous = null;
        foreach (var raw in Array(root.GetProperty("rows"), 128))
        {
            Fields(raw, ["document", "point"]);
            var document = CanonicalUuid(Text(raw.GetProperty("document")), "document");
            if (previous is not null && string.CompareOrdinal(previous, document) >= 0)
                throw Invalid("Query documents must be strictly increasing.");
            rows.Add(new NativeQueryRow(document, DecodePoint(raw.GetProperty("point"))));
            previous = document;
        }
        if (rows.Count > limit) throw Invalid("Query page exceeds the requested limit.");
        return new NativeQueryPage(rows.ToImmutable(), root.TryGetProperty("next_cursor", out var cursor) ? Cursor(Text(cursor)) : null);
    }

    /// <summary>Strictly decodes the complete native durability and transaction receipt.</summary>
    public static NativeCommitReceipt DecodeReceipt(JsonElement root)
    {
        Fields(root, ["version", "durability", "transaction_id", "event", "commit_sequence", "effect_digest", "authority"]);
        Version(root);
        var durability = Text(root.GetProperty("durability")) switch
        {
            "local_durable" => NativeDurability.LocalDurable,
            "object_durable" => NativeDurability.ObjectDurable,
            _ => throw Invalid("Unsupported native durability.")
        };
        return new NativeCommitReceipt(durability, DecodeIdentity(root), DecodeEvent(root.GetProperty("event")),
            U64(root.GetProperty("commit_sequence"), positive: true));
    }

    /// <summary>Strictly decodes an identity-bound retained or unresolved transaction status.</summary>
    public static NativeTransactionStatus DecodeStatus(JsonElement root)
    {
        Fields(root, ["version", "state", "transaction_id", "authority", "effect_digest", "outcome"]);
        Version(root);
        var identity = DecodeIdentity(root);
        var state = Text(root.GetProperty("state"));
        var outcome = root.GetProperty("outcome");
        if (state is "pending" or "unknown" or "expired")
        {
            if (outcome.ValueKind != JsonValueKind.Null) throw Invalid("Unresolved transaction status must have no outcome.");
            return new NativeTransactionStatus(identity, state switch
            {
                "pending" => NativeTransactionState.Pending,
                "expired" => NativeTransactionState.Expired,
                _ => NativeTransactionState.Unknown
            });
        }
        if (state == "committed")
        {
            Fields(outcome, ["receipt"]);
            var receipt = DecodeReceipt(outcome.GetProperty("receipt"));
            RequireIdentity(receipt.Identity, identity);
            return new NativeTransactionStatus(identity, NativeTransactionState.Committed, receipt);
        }
        if (state == "rejected")
        {
            Fields(outcome, ["error"]);
            Fields(outcome.GetProperty("error"), ["code"]);
            if (Text(outcome.GetProperty("error").GetProperty("code")) != "precondition_failed")
                throw Invalid("Unsupported retained rejection.");
            return new NativeTransactionStatus(identity, NativeTransactionState.Rejected);
        }
        throw Invalid("Unsupported native transaction status.");
    }

    /// <summary>Accepts only the published closed native error-code set.</summary>
    public static string DecodeError(JsonElement root)
    {
        Fields(root, ["error"]);
        var error = root.GetProperty("error");
        Fields(error, ["code"]);
        var code = Text(error.GetProperty("code"));
        if (!IsErrorCode(code)) throw Invalid("Unsupported native error code.");
        return code;
    }

    /// <summary>Tests membership in the exact published native Error.code enum.</summary>
    public static bool IsErrorCode(string? code) => code is
        "invalid_request" or "invalid_cursor" or "cursor_expired" or "invalid_key" or "forbidden_scope" or
        "unknown_route" or "method_not_allowed" or "schema_changed" or "idempotency_conflict" or
        "idempotency_expired" or "rate_limited" or "capacity" or "storage_unavailable" or "stored_corruption" or
        "commit_uncertain" or "precondition_failed" or "authority_changed" or "authority_unavailable";

    /// <summary>Rejects a response that differs from the original transaction identity.</summary>
    public static void RequireIdentity(NativeTransactionIdentity actual, NativeTransactionIdentity expected)
    {
        if (actual != expected) throw Invalid("Native response belongs to another transaction identity.");
    }

    /// <summary>Rejects response events outside the requested database and shard.</summary>
    public static void RequireScope(NativeEvent occurrence, NativeScope scope)
    {
        if (occurrence.Database != scope.Database || occurrence.Shard != scope.Shard)
            throw Invalid("Native response belongs to another database scope.");
    }

    /// <summary>Rejects response events outside the requested database and shard.</summary>
    public static void RequireScope(NativePoint point, NativeScope scope)
    {
        foreach (var sibling in point.Siblings) RequireScope(sibling.Event, scope);
    }

    /// <summary>Decodes a complete actual native commit request and verifies its supplied effect digest.</summary>
    public static NativeCommit DecodeCommit(NativeScope scope, JsonElement root) => DecodeCommit(scope, root, out _);

    internal static NativeCommit DecodeCommit(NativeScope scope, JsonElement root, out NativeTransactionIdentity identity)
    {
        Fields(root, ["version", "transaction_id", "authority", "effect_digest", "writes"]);
        Version(root);
        var authority = DecodeAuthority(root.GetProperty("authority"));
        var writes = ImmutableArray.CreateBuilder<NativeWrite>();
        foreach (var raw in Array(root.GetProperty("writes"), 1024, 1))
        {
            Fields(raw, ["collection", "document", "schema_generation", "schema_digest", "condition", "observed", "value"]);
            writes.Add(new NativeWrite(CanonicalUuid(Text(raw.GetProperty("collection")), "collection"),
                CanonicalUuid(Text(raw.GetProperty("document")), "document"), U64(raw.GetProperty("schema_generation"), true),
                Hex(Text(raw.GetProperty("schema_digest")), 32, "schema digest"), DecodeCondition(raw.GetProperty("condition")),
                DecodeObserved(raw.GetProperty("observed")), DecodeWriteValue(raw.GetProperty("value"))));
        }
        var commit = new NativeCommit(DecodeTransaction(root.GetProperty("transaction_id")), authority, writes.ToImmutable());
        identity = CommitIdentity(scope, commit);
        if (identity.EffectDigest != Hex(Text(root.GetProperty("effect_digest")), 32, "effect digest"))
            throw Invalid("Retained body contradicts its canonical effect digest.");
        return commit;
    }

    private static NativeTransactionIdentity DecodeIdentity(JsonElement root) => new(
        DecodeTransaction(root.GetProperty("transaction_id")), DecodeAuthority(root.GetProperty("authority")),
        Hex(Text(root.GetProperty("effect_digest")), 32, "effect digest"));

    private static NativeTransactionId DecodeTransaction(JsonElement root)
    {
        Fields(root, ["expires_at_unix_ms", "nonce"]);
        return new NativeTransactionId(U64(root.GetProperty("expires_at_unix_ms")), Hex(Text(root.GetProperty("nonce")), 16, "nonce"));
    }

    private static NativeEvent DecodeEvent(JsonElement root)
    {
        Fields(root, ["database", "shard", "replica", "incarnation", "sequence"]);
        return new NativeEvent(CanonicalUuid(Text(root.GetProperty("database")), "database"), U64(root.GetProperty("shard")),
            CanonicalUuid(Text(root.GetProperty("replica")), "replica"), Hex(Text(root.GetProperty("incarnation")), 16, "incarnation"),
            U64(root.GetProperty("sequence"), positive: true));
    }

    private static NativeAuthority DecodeAuthority(JsonElement root)
    {
        Fields(root, ["mode"], ["generation", "writer"]);
        var mode = Text(root.GetProperty("mode"));
        if (mode == "leaderless")
        {
            Fields(root, ["mode"]);
            return new NativeAuthority.Leaderless();
        }
        if (mode != "single_writer") throw Invalid("Unsupported native authority.");
        Fields(root, ["mode", "generation", "writer"]);
        var writer = root.GetProperty("writer");
        Fields(writer, ["replica", "incarnation"]);
        return new NativeAuthority.SingleWriter(U64(root.GetProperty("generation"), true),
            new NativeWriter(CanonicalUuid(Text(writer.GetProperty("replica")), "writer"), Hex(Text(writer.GetProperty("incarnation")), 16, "incarnation")));
    }

    private static NativeCondition DecodeCondition(JsonElement root)
    {
        Fields(root, ["kind"], ["version"]);
        var kind = Text(root.GetProperty("kind"));
        if (kind is "none" or "absent")
        {
            Fields(root, ["kind"]);
            return kind == "none" ? new NativeCondition.None() : new NativeCondition.Absent();
        }
        Fields(root, ["kind", "version"]);
        if (kind != "version") throw Invalid("Unsupported native condition.");
        return new NativeCondition.Version(Hex(Text(root.GetProperty("version")), 32, "condition version"));
    }

    private static NativeWriteValue DecodeWriteValue(JsonElement root, bool sibling = false)
    {
        var prefix = sibling ? new[] { "event", "schema_generation", "schema_digest" } : [];
        Fields(root, [.. prefix, "kind"], ["fields"]);
        var kind = Text(root.GetProperty("kind"));
        if (kind == "tombstone")
        {
            Fields(root, [.. prefix, "kind"]);
            return new NativeWriteValue.Tombstone();
        }
        if (kind != "live") throw Invalid("Unsupported native write kind.");
        Fields(root, [.. prefix, "kind", "fields"]);
        var fields = DecodeFields(root.GetProperty("fields"), 1, new Budget());
        ValidateDocument(fields);
        return new NativeWriteValue.Live(fields);
    }

    private static ImmutableArray<NativeObserved> DecodeObserved(JsonElement root, int maximum = 1024)
    {
        var entries = ImmutableArray.CreateBuilder<NativeObserved>();
        foreach (var raw in Array(root, maximum))
        {
            Fields(raw, ["replica", "incarnation", "sequence"]);
            entries.Add(new NativeObserved(CanonicalUuid(Text(raw.GetProperty("replica")), "replica"),
                Hex(Text(raw.GetProperty("incarnation")), 16, "incarnation"), U64(raw.GetProperty("sequence"), true)));
        }
        var result = entries.ToImmutable();
        ValidateObserved(result, maximum);
        return result;
    }

    private static ImmutableArray<NativeField> DecodeFields(JsonElement root, int depth, Budget budget)
    {
        uint previous = 0;
        var fields = ImmutableArray.CreateBuilder<NativeField>();
        foreach (var raw in Array(root))
        {
            Fields(raw, ["id", "value"]);
            var id = raw.GetProperty("id");
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var number) || number <= previous)
                throw Invalid("Field IDs must be strictly increasing positive u32 integers.");
            previous = number;
            fields.Add(new NativeField(number, DecodeValue(raw.GetProperty("value"), depth, budget)));
        }
        return fields.ToImmutable();
    }

    private static NativeValue DecodeValue(JsonElement root, int depth, Budget budget)
    {
        budget.Take(depth);
        Fields(root, ["type"], ["value", "fields", "items"]);
        var type = Text(root.GetProperty("type"));
        switch (type)
        {
            case "null": Fields(root, ["type"]); return new NativeValue.Null();
            case "bool":
                Fields(root, ["type", "value"]);
                var boolean = root.GetProperty("value");
                if (boolean.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("Boolean value must be boolean.");
                return new NativeValue.Boolean(boolean.GetBoolean());
            case "i64":
                Fields(root, ["type", "value"]);
                var decimalText = Text(root.GetProperty("value"));
                if (!long.TryParse(decimalText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) || Decimal(integer) != decimalText)
                    throw Invalid("i64 must be canonical decimal.");
                return new NativeValue.Integer(integer);
            case "utf8": Fields(root, ["type", "value"]); return new NativeValue.Text(ValidateText(Text(root.GetProperty("value"))));
            case "bytes": Fields(root, ["type", "value"]); return new NativeValue.Bytes(ParseBytes(Text(root.GetProperty("value"))).ToImmutableArray());
            case "map": Fields(root, ["type", "fields"]); return new NativeValue.Map(DecodeFields(root.GetProperty("fields"), depth + 1, budget));
            case "array":
                Fields(root, ["type", "items"]);
                return new NativeValue.Array(Array(root.GetProperty("items")).Select(item => DecodeValue(item, depth + 1, budget)).ToImmutableArray());
            default: throw Invalid("Unsupported native value type.");
        }
    }

    private static object WireTransaction(NativeTransactionId transaction) => Object(
        ("expires_at_unix_ms", Decimal(transaction.ExpiresAtUnixMs)), ("nonce", Hex(transaction.Nonce, 16, "nonce")));

    private static object WireAuthority(NativeAuthority authority) => authority switch
    {
        NativeAuthority.Leaderless => Object(("mode", "leaderless")),
        NativeAuthority.SingleWriter writer when writer.Generation > 0 => Object(("mode", "single_writer"),
            ("generation", Decimal(writer.Generation)), ("writer", Object(("replica", CanonicalUuid(writer.Writer.Replica, "writer")),
                ("incarnation", Hex(writer.Writer.Incarnation, 16, "incarnation"))))),
        _ => throw Invalid("Invalid native mutation authority.")
    };

    private static object WireCondition(NativeCondition condition) => condition switch
    {
        NativeCondition.None => Object(("kind", "none")),
        NativeCondition.Absent => Object(("kind", "absent")),
        NativeCondition.Version version => Object(("kind", "version"), ("version", Hex(version.Token, 32, "condition version"))),
        _ => throw Invalid("Invalid native document condition.")
    };

    private static void ValidateObserved(ImmutableArray<NativeObserved> entries, int maximum = 1024)
    {
        Bounded(entries, maximum);
        string? previous = null;
        foreach (var entry in entries)
        {
            var order = CanonicalUuid(entry.Replica, "replica") + Hex(entry.Incarnation, 16, "incarnation");
            if (entry.Sequence == 0 || (previous is not null && string.CompareOrdinal(previous, order) >= 0))
                throw Invalid("Observed origins must be strictly increasing with positive sequence.");
            previous = order;
        }
    }

    private static void ValidateWriteMetadata(NativeWrite write, NativeAuthority authority)
    {
        if (write is null) throw Invalid("Native write must be present.");
        CanonicalUuid(write.Collection, "collection");
        CanonicalUuid(write.Document, "document");
        Hex(write.SchemaDigest, 32, "schema digest");
        if (write.SchemaGeneration == 0) throw Invalid("Schema generation must be positive.");
        if (authority is NativeAuthority.SingleWriter && write.Condition is NativeCondition.None)
            throw Invalid("Single-writer mutations require an atomic condition.");
        if (write.Condition is NativeCondition.Version version) Hex(version.Token, 32, "condition version");
        else if (write.Condition is not (NativeCondition.None or NativeCondition.Absent))
            throw Invalid("Invalid native document condition.");
        ValidateObserved(write.Observed);
        if (write.Value is not (NativeWriteValue.Live or NativeWriteValue.Tombstone))
            throw Invalid("Unsupported native write kind.");
    }

    private static object WireWrite(NativeWrite write, NativeAuthority authority)
    {
        if (authority is NativeAuthority.SingleWriter && write.Condition is NativeCondition.None)
            throw Invalid("Single-writer mutations require an atomic condition.");
        if (write.SchemaGeneration == 0) throw Invalid("Schema generation must be positive.");
        object value;
        if (write.Value is NativeWriteValue.Live live)
        {
            value = Object(("kind", "live"), ("fields", WireFields(live.Fields, 1, new Budget())));
        }
        else if (write.Value is NativeWriteValue.Tombstone) value = Object(("kind", "tombstone"));
        else throw Invalid("Unsupported native write kind.");
        return Object(("collection", CanonicalUuid(write.Collection, "collection")), ("document", CanonicalUuid(write.Document, "document")),
            ("schema_generation", Decimal(write.SchemaGeneration)), ("schema_digest", Hex(write.SchemaDigest, 32, "schema digest")),
            ("condition", WireCondition(write.Condition)), ("observed", WireObserved(write.Observed)), ("value", value));
    }

    private static object[] WireObserved(ImmutableArray<NativeObserved> entries, int maximum = 1024)
    {
        Bounded(entries, maximum);
        string? previous = null;
        var result = new object[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var order = CanonicalUuid(entry.Replica, "replica") + Hex(entry.Incarnation, 16, "incarnation");
            if (entry.Sequence == 0 || (previous is not null && string.CompareOrdinal(previous, order) >= 0))
                throw Invalid("Observed origins must be strictly increasing with positive sequence.");
            previous = order;
            result[index] = Object(("replica", entry.Replica), ("incarnation", entry.Incarnation), ("sequence", Decimal(entry.Sequence)));
        }
        return result;
    }

    private static object[] WireFields(ImmutableArray<NativeField> fields, int depth, Budget budget)
    {
        Bounded(fields, 1024);
        uint previous = 0;
        var result = new object[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Id <= previous) throw Invalid("Field IDs must be strictly increasing positive u32 integers.");
            previous = field.Id;
            result[index] = Object(("id", field.Id), ("value", WireValue(field.Value, depth, budget)));
        }
        return result;
    }

    private static object WireValue(NativeValue value, int depth, Budget budget)
    {
        budget.Take(depth);
        return value switch
        {
            NativeValue.Null => Object(("type", "null")),
            NativeValue.Boolean boolean => Object(("type", "bool"), ("value", boolean.Value)),
            NativeValue.Integer integer => Object(("type", "i64"), ("value", Decimal(integer.Value))),
            NativeValue.Text text => Object(("type", "utf8"), ("value", ValidateText(text.Value))),
            NativeValue.Bytes bytes when !bytes.Value.IsDefault && bytes.Value.Length <= MaximumValueBytes =>
                Object(("type", "bytes"), ("value", Base64Url(bytes.Value.AsSpan()))),
            NativeValue.Map map => Object(("type", "map"), ("fields", WireFields(map.Fields, depth + 1, budget))),
            NativeValue.Array array => Object(("type", "array"), ("items", WireArray(array.Items, depth + 1, budget))),
            _ => throw Invalid("Invalid native value.")
        };
    }

    private static object[] WireArray(ImmutableArray<NativeValue> values, int depth, Budget budget)
    {
        Bounded(values, 1024);
        return values.Select(value => WireValue(value, depth, budget)).ToArray();
    }

    private static void ValidateDocument(ImmutableArray<NativeField> fields)
    {
        Bounded(fields, 1024);
        using var bytes = new Buffer(MaximumValueBytes);
        var budget = new Budget();
        long size = 98L + 13L * fields.Length;
        uint previous = 0;
        foreach (var field in fields)
        {
            if (field.Id <= previous) throw Invalid("Field IDs must be strictly increasing positive u32 integers.");
            previous = field.Id;
            var start = bytes.Length;
            WriteNativeValue(bytes, field.Value, 1, budget);
            size += bytes.Length - start;
            if (size > MaximumValueBytes) throw Invalid("Encoded native document exceeds the 1 MiB limit.");
        }
    }

    // Nested framing is backfilled in the same buffer; payloads are never copied per ancestor.
    private static byte WriteNativeValue(Buffer bytes, NativeValue value, int depth, Budget budget, bool insideArray = false)
    {
        budget.Take(depth);
        var start = bytes.Length;
        byte type;
        void Nested(NativeValue nested, bool inArray)
        {
            var typePosition = bytes.Length;
            bytes.Byte(0);
            var lengthPosition = bytes.Length;
            bytes.UInt32(0);
            var payload = bytes.Length;
            var nestedType = WriteNativeValue(bytes, nested, depth + 1, budget, inArray);
            bytes.PatchByte(typePosition, nestedType);
            bytes.PatchUInt32(lengthPosition, checked((uint)(bytes.Length - payload)));
        }
        switch (value)
        {
            case NativeValue.Null: type = 1; break;
            case NativeValue.Boolean boolean: type = 2; bytes.Byte(boolean.Value ? (byte)1 : (byte)0); break;
            case NativeValue.Integer integer: type = 3; bytes.Int64(integer.Value); break;
            case NativeValue.Text text: type = 4; bytes.Put(Utf8.GetBytes(ValidateText(text.Value))); break;
            case NativeValue.Bytes binary:
                if (binary.Value.IsDefault) throw Invalid("Bytes must be initialized.");
                type = 5; bytes.Put(binary.Value.AsSpan()); break;
            case NativeValue.Map map:
                Bounded(map.Fields, 1024);
                type = 6; bytes.UInt16((ushort)map.Fields.Length);
                uint previous = 0;
                foreach (var field in map.Fields)
                {
                    if (field.Id <= previous) throw Invalid("Field IDs must be strictly increasing positive u32 integers.");
                    previous = field.Id;
                    bytes.UInt32(field.Id);
                    Nested(field.Value, insideArray);
                }
                break;
            case NativeValue.Array array:
                if (insideArray) throw Invalid("Arrays cannot contain descendant arrays, including through maps.");
                Bounded(array.Items, 1024);
                type = 7; bytes.UInt16((ushort)array.Items.Length);
                foreach (var item in array.Items) Nested(item, true);
                break;
            default: throw Invalid("Invalid native value.");
        }
        if (bytes.Length - start > MaximumValueBytes) throw Invalid("Native encoding exceeds its byte limit.");
        return type;
    }

    private static string EffectDigest(NativeScope scope, NativeAuthority authority, ImmutableArray<NativeWrite> writes)
    {
        using var bytes = new Buffer(MaximumBodyBytes * 2);
        bytes.Put("TB-DOC-EFFECT-v1\0"u8);
        bytes.Uuid(scope.Tenant); bytes.Uuid(scope.Database); bytes.UInt64(scope.Shard);
        bytes.Byte(authority is NativeAuthority.SingleWriter ? (byte)1 : (byte)0);
        if (authority is NativeAuthority.SingleWriter writer)
        {
            bytes.UInt64(writer.Generation); bytes.Uuid(writer.Writer.Replica); bytes.Hex(writer.Writer.Incarnation);
        }
        bytes.UInt64((ulong)writes.Length);
        string? previousWrite = null;
        foreach (var write in writes)
        {
            ValidateWriteMetadata(write, authority);
            var order = write.Collection + write.Document;
            if (previousWrite is not null && string.CompareOrdinal(previousWrite, order) >= 0)
                throw Invalid("Writes must be strictly sorted by collection and document.");
            previousWrite = order;
            bytes.Uuid(write.Collection); bytes.Uuid(write.Document); bytes.UInt64(write.SchemaGeneration); bytes.Hex(write.SchemaDigest);
            bytes.Byte(write.Condition is NativeCondition.None ? (byte)0 : write.Condition is NativeCondition.Absent ? (byte)1 : (byte)2);
            if (write.Condition is NativeCondition.Version version) bytes.Hex(version.Token);
            bytes.UInt64((ulong)write.Observed.Length);
            foreach (var observed in write.Observed)
            {
                bytes.Uuid(observed.Replica); bytes.Hex(observed.Incarnation); bytes.UInt64(observed.Sequence);
            }
            bytes.Byte(write.Value is NativeWriteValue.Live ? (byte)1 : (byte)0);
            if (write.Value is NativeWriteValue.Live live)
            {
                Bounded(live.Fields, 1024);
                bytes.UInt64((ulong)live.Fields.Length);
                var budget = new Budget();
                long size = 98L + 13L * live.Fields.Length;
                uint previous = 0;
                foreach (var field in live.Fields)
                {
                    if (field.Id <= previous) throw Invalid("Field IDs must be strictly increasing positive u32 integers.");
                    previous = field.Id;
                    bytes.UInt32(field.Id);
                    var typePosition = bytes.Length;
                    bytes.Byte(0);
                    var lengthPosition = bytes.Length;
                    bytes.UInt64(0);
                    var start = bytes.Length;
                    var type = WriteNativeValue(bytes, field.Value, 1, budget);
                    var length = bytes.Length - start;
                    bytes.PatchByte(typePosition, type);
                    bytes.PatchUInt64(lengthPosition, (ulong)length);
                    size += length;
                    if (size > MaximumValueBytes) throw Invalid("Encoded native document exceeds the 1 MiB limit.");
                }
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.Written));
    }

    private static Dictionary<string, object?> Object(params (string Name, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);

    private static string Json(object value)
    {
        using var stream = new BoundedJsonStream(MaximumBodyBytes);
        JsonSerializer.Serialize(stream, value);
        return Utf8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new BoundedJsonStream(MaximumBodyBytes);
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
            writer.Flush();
        }
        return Utf8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void Fields(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Expected a native protocol object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name) || (!required.Contains(property.Name, StringComparer.Ordinal) &&
                !(optional?.Contains(property.Name, StringComparer.Ordinal) ?? false)))
                throw Invalid("Native object contains unknown or duplicate fields.");
        }
        if (required.Any(name => !names.Contains(name))) throw Invalid("Native object has missing fields.");
    }

    private static void Version(JsonElement root)
    {
        var version = root.GetProperty("version");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            throw Invalid("Unsupported native protocol version.");
    }

    private static IEnumerable<JsonElement> Array(JsonElement value, int maximum = 1024, int minimum = 0)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum || value.GetArrayLength() > maximum)
            throw Invalid("Native array exceeds its bounds.");
        return value.EnumerateArray();
    }

    private static void Bounded<T>(ImmutableArray<T> entries, int maximum, int minimum = 0)
    {
        if (entries.IsDefault || entries.Length < minimum || entries.Length > maximum) throw Invalid("Native array exceeds its bounds.");
    }

    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid("Expected native protocol text.");
        try { return value.GetString()!; }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("Native protocol text contains invalid Unicode.", exception);
        }
    }

    private static string ValidateText(string value)
    {
        if (Utf8.GetByteCount(value) > MaximumValueBytes) throw Invalid("Native text exceeds its byte limit.");
        return value;
    }

    private static ulong U64(JsonElement value, bool positive = false)
    {
        var text = Text(value);
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || Decimal(number) != text || (positive && number == 0))
            throw Invalid("u64 must be canonical decimal text within its range.");
        return number;
    }

    private static string Hex(string value, int bytes, string name)
    {
        if (value is null || value.Length != bytes * 2 || value.Any(character => !IsLowerHex(character)))
            throw Invalid($"{name} must be canonical lowercase hexadecimal.");
        return value;
    }

    private static bool IsLowerHex(char character) => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f';
    private static string Decimal(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void FieldId(uint value) { if (value == 0) throw Invalid("Field ID must be positive."); }
    private static string Base64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] ParseBytes(string value)
    {
        if (value.Length > 1_398_102 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw Invalid("Bytes must be bounded canonical base64url.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4)); }
        catch (FormatException) { throw Invalid("Bytes must be canonical base64url."); }
        if (bytes.Length > MaximumValueBytes || Base64Url(bytes) != value) throw Invalid("Bytes must be bounded canonical base64url.");
        return bytes;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class BoundedJsonStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > maximum) throw Invalid("Native request exceeds the 4 MiB limit.");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > maximum) throw Invalid("Native request exceeds the 4 MiB limit.");
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            if (Length >= maximum) throw Invalid("Native request exceeds the 4 MiB limit.");
            base.WriteByte(value);
        }
    }

    private sealed class Budget
    {
        private int nodes;
        public void Take(int depth)
        {
            if (depth > 16 || ++nodes > 4096) throw Invalid("Native document exceeds its depth or node limit.");
        }
    }

    private sealed class Buffer(int maximum) : IDisposable
    {
        private readonly MemoryStream stream = new();

        public void Put(ReadOnlySpan<byte> bytes)
        {
            if (stream.Length + bytes.Length > maximum) throw Invalid("Native encoding exceeds its byte limit.");
            stream.Write(bytes);
        }
        public long Length => stream.Length;
        public ReadOnlySpan<byte> Written => stream.GetBuffer().AsSpan(0, checked((int)stream.Length));
        public void PatchByte(long position, byte value) => stream.GetBuffer()[checked((int)position)] = value;
        public void PatchUInt32(long position, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stream.GetBuffer().AsSpan(checked((int)position), 4), value);
        public void PatchUInt64(long position, ulong value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(stream.GetBuffer().AsSpan(checked((int)position), 8), value);
        public void Byte(byte value) => Put([value]);
        public void UInt16(ushort value) { Span<byte> bytes = stackalloc byte[2]; System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); Put(bytes); }
        public void UInt32(uint value) { Span<byte> bytes = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Put(bytes); }
        public void UInt64(ulong value) { Span<byte> bytes = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); Put(bytes); }
        public void Int64(long value) { Span<byte> bytes = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value); Put(bytes); }
        public void Hex(string value) => Put(Convert.FromHexString(value));
        public void Uuid(string value) => Hex(value.Replace("-", string.Empty, StringComparison.Ordinal));
        public byte[] Finish() => stream.ToArray();
        public void Dispose() => stream.Dispose();
    }
}
