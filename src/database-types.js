/**
 * The current TerraBase document protocol uses canonical UUID entity IDs.
 * @typedef {string} CanonicalUuid
 */

/**
 * All 64-bit quantities stay bigint in JavaScript and decimal strings on the wire.
 * @typedef {{
 *   readonly tenant: CanonicalUuid;
 *   readonly database: CanonicalUuid;
 *   readonly shard: bigint;
 * }} DatabaseScope
 */

/**
 * @typedef {{
 *   readonly collection: CanonicalUuid;
 *   readonly schema_generation: bigint;
 *   readonly schema_digest: string;
 * }} CollectionBinding
 */

/**
 * @typedef {(
 *   | { readonly type: "null" }
 *   | { readonly type: "bool"; readonly value: boolean }
 *   | { readonly type: "i64"; readonly value: bigint }
 *   | { readonly type: "utf8"; readonly value: string }
 *   | { readonly type: "bytes"; readonly value: Uint8Array }
 *   | { readonly type: "map"; readonly fields: readonly DatabaseField[] }
 *   | { readonly type: "array"; readonly items: readonly DatabaseValue[] }
 * )} DatabaseValue
 */

/**
 * Missing fields are omitted; an explicit null remains a typed null value.
 * @typedef {{ readonly id: number; readonly value: DatabaseValue }} DatabaseField
 */

/**
 * @typedef {{
 *   readonly replica: CanonicalUuid;
 *   readonly incarnation: string;
 *   readonly sequence: bigint;
 * }} DatabaseObserved
 */

/**
 * @typedef {DatabaseObserved & {
 *   readonly database: CanonicalUuid;
 *   readonly shard: bigint;
 * }} DatabaseEvent
 */

/**
 * @typedef {(
 *   | { readonly mode: "leaderless" }
 *   | {
 *     readonly mode: "single_writer";
 *     readonly generation: bigint;
 *     readonly writer: { readonly replica: CanonicalUuid; readonly incarnation: string };
 *   }
 * )} DatabaseAuthority
 */

/**
 * @typedef {(
 *   | { readonly kind: "none" }
 *   | { readonly kind: "absent" }
 *   | { readonly kind: "version"; readonly version: string }
 * )} DatabaseCondition
 */

/**
 * @typedef {(
 *   | { readonly kind: "live"; readonly fields: readonly DatabaseField[] }
 *   | { readonly kind: "tombstone" }
 * )} DatabaseWriteValue
 */

/**
 * @typedef {CollectionBinding & {
 *   readonly document: CanonicalUuid;
 *   readonly condition: DatabaseCondition;
 *   readonly observed: readonly DatabaseObserved[];
 *   readonly value: DatabaseWriteValue;
 * }} DatabaseWrite
 */

/** @typedef {{ readonly expires_at_unix_ms: bigint; readonly nonce: string }} DatabaseTransactionId */

/**
 * @typedef {{
 *   readonly transaction_id: DatabaseTransactionId;
 *   readonly authority: DatabaseAuthority;
 *   readonly writes: readonly DatabaseWrite[];
 * }} DatabaseCommit
 */

/**
 * @typedef {Omit<CollectionBinding, "collection"> & {
 *   readonly event: DatabaseEvent;
 * } & DatabaseWriteValue} DatabaseSibling
 */

/**
 * `state_version`: only a unique live or deleted state carries an atomic condition token.
 * @typedef {{
 *   readonly version: 1;
 *   readonly state: "absent" | "deleted" | "value" | "conflict";
 *   readonly state_version: string | null;
 *   readonly context: readonly DatabaseObserved[];
 *   readonly siblings: readonly DatabaseSibling[];
 * }} DatabasePoint
 */

/** @typedef {Extract<DatabaseValue, { readonly type: "bool" | "i64" | "utf8" | "bytes" }>} DatabaseQueryValue */

/**
 * @typedef {(
 *   | {
 *     readonly op: "equal" | "less_than" | "less_than_or_equal" | "greater_than" | "greater_than_or_equal";
 *     readonly path: readonly number[];
 *     readonly value: DatabaseQueryValue;
 *   }
 *   | { readonly op: "is_null" | "is_missing"; readonly path: readonly number[] }
 * )} DatabaseQueryPredicate
 */

/**
 * @typedef {{
 *   readonly predicate: DatabaseQueryPredicate;
 *   readonly limit: number;
 *   readonly cursor?: string;
 * }} DatabaseQuery
 */

/** @typedef {{ readonly document: CanonicalUuid; readonly point: DatabasePoint }} DatabaseQueryRow */

/**
 * @typedef {{
 *   readonly version: 1;
 *   readonly rows: readonly DatabaseQueryRow[];
 *   readonly next_cursor?: string;
 * }} DatabaseQueryPage
 */

/**
 * @typedef {{
 *   readonly version: 1;
 *   readonly durability: "local_durable" | "object_durable";
 *   readonly transaction_id: DatabaseTransactionId;
 *   readonly event: DatabaseEvent;
 *   readonly commit_sequence: bigint;
 *   readonly effect_digest: string;
 *   readonly authority: DatabaseAuthority;
 * }} DatabaseCommitReceipt
 */

/**
 * @typedef {{
 *   readonly transaction_id: DatabaseTransactionId;
 *   readonly authority: DatabaseAuthority;
 *   readonly effect_digest: string;
 * }} DatabaseTransactionIdentity
 */

/**
 * @typedef {DatabaseTransactionIdentity & { readonly version: 1 } & (
 *   | { readonly state: "committed"; readonly outcome: { readonly receipt: DatabaseCommitReceipt } }
 *   | { readonly state: "rejected"; readonly outcome: { readonly error: { readonly code: "precondition_failed" } } }
 *   | { readonly state: "pending" | "unknown" | "expired"; readonly outcome: null }
 * )} DatabaseTransactionStatus
 */

/**
 * An uncertain request needs status resolution; it is never an aborted transaction.
 * @template T
 * @typedef {(
 *   | { readonly ok: true; readonly status: number; readonly value: T }
 *   | {
 *     readonly ok: false;
 *     readonly error: {
 *       readonly code: string;
 *       readonly status?: number;
 *       readonly mutation_outcome?: "rejected" | "uncertain" | "unknown";
 *       readonly transaction?: DatabaseTransactionIdentity;
 *     };
 *   }
 * )} DatabaseResult
 */

/**
 * @typedef {(
 *   | {
 *     readonly phase: "snapshot";
 *     readonly predicate: DatabaseQueryPredicate;
 *     readonly limit: number;
 *     readonly snapshot_cursor?: string;
 *   }
 *   | { readonly phase: "poll"; readonly predicate: DatabaseQueryPredicate; readonly poll_cursor: string }
 * )} DatabaseSubscriptionRequest
 */

/** @typedef {{ readonly document: CanonicalUuid; readonly point: DatabasePoint | null }} DatabaseSubscriptionEffect */

/**
 * @typedef {{
 *   readonly sequence: bigint;
 *   readonly effects: readonly DatabaseSubscriptionEffect[];
 * }} DatabaseSubscriptionGroup
 */

/**
 * @typedef {(
 *   | {
 *     readonly version: 1;
 *     readonly phase: "snapshot";
 *     readonly rows: readonly DatabaseQueryRow[];
 *     readonly next_snapshot_cursor: string;
 *   }
 *   | {
 *     readonly version: 1;
 *     readonly phase: "snapshot";
 *     readonly rows: readonly DatabaseQueryRow[];
 *     readonly poll_cursor: string;
 *   }
 *   | {
 *     readonly version: 1;
 *     readonly phase: "poll";
 *     readonly groups: readonly DatabaseSubscriptionGroup[];
 *     readonly poll_cursor: string;
 *   }
 * )} DatabaseSubscriptionResponse
 */

export {};
