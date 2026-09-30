/** The current TerraBase document protocol uses canonical UUID entity IDs. */
export type CanonicalUuid = string;

/** All 64-bit quantities stay bigint in JavaScript and decimal strings on the wire. */
export type DatabaseScope = {
  readonly tenant: CanonicalUuid;
  readonly database: CanonicalUuid;
  readonly shard: bigint;
};

export type CollectionBinding = {
  readonly collection: CanonicalUuid;
  readonly schema_generation: bigint;
  readonly schema_digest: string;
};

export type DatabaseValue =
  | { readonly type: "null" }
  | { readonly type: "bool"; readonly value: boolean }
  | { readonly type: "i64"; readonly value: bigint }
  | { readonly type: "utf8"; readonly value: string }
  | { readonly type: "bytes"; readonly value: Uint8Array }
  | { readonly type: "map"; readonly fields: readonly DatabaseField[] }
  | { readonly type: "array"; readonly items: readonly DatabaseValue[] };

/** Missing fields are omitted; an explicit null remains a typed null value. */
export type DatabaseField = { readonly id: number; readonly value: DatabaseValue };

export type DatabaseObserved = {
  readonly replica: CanonicalUuid;
  readonly incarnation: string;
  readonly sequence: bigint;
};

export type DatabaseEvent = DatabaseObserved & {
  readonly database: CanonicalUuid;
  readonly shard: bigint;
};

export type DatabaseAuthority =
  | { readonly mode: "leaderless" }
  | {
    readonly mode: "single_writer";
    readonly generation: bigint;
    readonly writer: { readonly replica: CanonicalUuid; readonly incarnation: string };
  };

export type DatabaseCondition =
  | { readonly kind: "none" }
  | { readonly kind: "absent" }
  | { readonly kind: "version"; readonly version: string };

export type DatabaseWriteValue =
  | { readonly kind: "live"; readonly fields: readonly DatabaseField[] }
  | { readonly kind: "tombstone" };

export type DatabaseWrite = CollectionBinding & {
  readonly document: CanonicalUuid;
  readonly condition: DatabaseCondition;
  readonly observed: readonly DatabaseObserved[];
  readonly value: DatabaseWriteValue;
};

export type DatabaseTransactionId = { readonly expires_at_unix_ms: bigint; readonly nonce: string };

export type DatabaseCommit = {
  readonly transaction_id: DatabaseTransactionId;
  readonly authority: DatabaseAuthority;
  readonly writes: readonly DatabaseWrite[];
};

export type DatabaseSibling = Omit<CollectionBinding, "collection"> & {
  readonly event: DatabaseEvent;
} & DatabaseWriteValue;

export type DatabasePoint = {
  readonly version: 1;
  readonly state: "absent" | "deleted" | "value" | "conflict";
  /** Only a unique live or deleted state carries an atomic condition token. */
  readonly state_version: string | null;
  readonly context: readonly DatabaseObserved[];
  readonly siblings: readonly DatabaseSibling[];
};

export type DatabaseQueryValue = Extract<DatabaseValue, { readonly type: "bool" | "i64" | "utf8" | "bytes" }>;
export type DatabaseQueryPredicate =
  | {
    readonly op: "equal" | "less_than" | "less_than_or_equal" | "greater_than" | "greater_than_or_equal";
    readonly path: readonly number[];
    readonly value: DatabaseQueryValue;
  }
  | { readonly op: "is_null" | "is_missing"; readonly path: readonly number[] };

export type DatabaseQuery = {
  readonly predicate: DatabaseQueryPredicate;
  readonly limit: number;
  readonly cursor?: string;
};

export type DatabaseQueryRow = { readonly document: CanonicalUuid; readonly point: DatabasePoint };
export type DatabaseQueryPage = {
  readonly version: 1;
  readonly rows: readonly DatabaseQueryRow[];
  readonly next_cursor?: string;
};

export type DatabaseCommitReceipt = {
  readonly version: 1;
  readonly durability: "local_durable" | "object_durable";
  readonly transaction_id: DatabaseTransactionId;
  readonly event: DatabaseEvent;
  readonly commit_sequence: bigint;
  readonly effect_digest: string;
  readonly authority: DatabaseAuthority;
};

export type DatabaseTransactionIdentity = {
  readonly transaction_id: DatabaseTransactionId;
  readonly authority: DatabaseAuthority;
  readonly effect_digest: string;
};

export type DatabaseTransactionStatus = DatabaseTransactionIdentity & { readonly version: 1 } & (
  | { readonly state: "committed"; readonly outcome: { readonly receipt: DatabaseCommitReceipt } }
  | { readonly state: "rejected"; readonly outcome: { readonly error: { readonly code: "precondition_failed" } } }
  | { readonly state: "pending" | "unknown" | "expired"; readonly outcome: null }
);

/** An uncertain request needs status resolution; it is never an aborted transaction. */
export type DatabaseResult<T> =
  | { readonly ok: true; readonly status: number; readonly value: T }
  | {
    readonly ok: false;
    readonly error: {
      readonly code: string;
      readonly status?: number;
      readonly mutation_outcome?: "rejected" | "uncertain" | "unknown";
      readonly transaction?: DatabaseTransactionIdentity;
    };
  };

export type DatabaseSubscriptionRequest =
  | {
    readonly phase: "snapshot";
    readonly predicate: DatabaseQueryPredicate;
    readonly limit: number;
    readonly snapshot_cursor?: string;
  }
  | { readonly phase: "poll"; readonly predicate: DatabaseQueryPredicate; readonly poll_cursor: string };

export type DatabaseSubscriptionEffect = { readonly document: CanonicalUuid; readonly point: DatabasePoint | null };
export type DatabaseSubscriptionGroup = {
  readonly sequence: bigint;
  readonly effects: readonly DatabaseSubscriptionEffect[];
};
export type DatabaseSubscriptionResponse =
  | {
    readonly version: 1;
    readonly phase: "snapshot";
    readonly rows: readonly DatabaseQueryRow[];
    readonly next_snapshot_cursor: string;
  }
  | {
    readonly version: 1;
    readonly phase: "snapshot";
    readonly rows: readonly DatabaseQueryRow[];
    readonly poll_cursor: string;
  }
  | {
    readonly version: 1;
    readonly phase: "poll";
    readonly groups: readonly DatabaseSubscriptionGroup[];
    readonly poll_cursor: string;
  };
