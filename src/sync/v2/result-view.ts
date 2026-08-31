/**
 * The public result-view state machine for `terrascale.sync.v2`.
 *
 * This module contains only authorized query results. It has no collection
 * journal, replication identity, causal vector, or durable client write
 * queue. Checkpoints are opaque capabilities issued by the server; equality
 * is the only operation the client needs.
 */

const textEncoder = new TextEncoder();
const maxU64 = 0xffff_ffff_ffff_ffffn;
const maxPendingMutations = 1024;

export const syncV2ResultViewLimits = Object.freeze({
  maxItems: 10_000,
  maxValueBytes: 64 * 1024,
  maxOperations: 1_024,
  maxIdentityBytes: 256,
  maxFieldPathBytes: 256,
  maxCheckpointBytes: 4_096,
} as const);

/** A bounded projected field. Bytes are used so the result digest is cross-language stable. */
export type SyncV2ProjectedField = Uint8Array;

/** One authorized result row, in canonical query order. */
export type SyncV2ResultItem = {
  readonly identity: string;
  readonly value: Readonly<Record<string, SyncV2ProjectedField>>;
};

/** A complete authorized snapshot. */
export type SyncV2ResultSnapshot = {
  readonly view_generation: bigint;
  readonly authorization_generation: bigint;
  readonly revision: bigint;
  readonly items: readonly SyncV2ResultItem[];
  /** Lowercase hexadecimal digest over the complete ordered result. */
  readonly digest: string;
  readonly checkpoint: SyncV2ViewCheckpoint;
};

export type SyncV2ResultFieldChange = {
  readonly path: string;
  readonly value: SyncV2ProjectedField | null;
};

export type SyncV2ResultOperation =
  | { readonly kind: "Insert"; readonly identity: string; readonly position: number; readonly value: Readonly<Record<string, SyncV2ProjectedField>> }
  | { readonly kind: "Update"; readonly identity: string; readonly value: Readonly<Record<string, SyncV2ProjectedField>> }
  | { readonly kind: "Patch"; readonly identity: string; readonly changes: readonly SyncV2ResultFieldChange[] }
  | { readonly kind: "Remove"; readonly identity: string }
  | { readonly kind: "Move"; readonly identity: string; readonly position: number }
  | { readonly kind: "Reset"; readonly snapshot: SyncV2ResultSnapshot };

/** One atomic authorized result revision. */
export type SyncV2ResultDelta = {
  readonly view_generation: bigint;
  readonly from_revision: bigint;
  readonly to_revision: bigint;
  readonly operations: readonly SyncV2ResultOperation[];
  readonly digest: string;
  readonly checkpoint: SyncV2ViewCheckpoint;
};

/** The opaque checkpoint token. No fields, merge, or causal decoding are exposed. */
export class SyncV2ViewCheckpoint {
  readonly #bytes: Uint8Array;

  public constructor(token: Uint8Array | string) {
    const bytes = typeof token === "string" ? textEncoder.encode(token) : new Uint8Array(token);
    if (bytes.length === 0 || bytes.length > syncV2ResultViewLimits.maxCheckpointBytes) {
      throw new SyncV2ViewError("invalid_checkpoint", "the checkpoint token is outside its bound");
    }
    this.#bytes = bytes;
  }

  /** Equality is intentionally the only semantic comparison. */
  public equals(other: SyncV2ViewCheckpoint): boolean {
    if (this.#bytes.length !== other.#bytes.length) return false;
    return this.#bytes.every((byte, index) => byte === other.#bytes[index]);
  }

  /** Returns a copy for transport persistence; the contents have no public structure. */
  public toBytes(): Uint8Array {
    return new Uint8Array(this.#bytes);
  }

  public toBase64(): string {
    let binary = "";
    for (const byte of this.#bytes) binary += String.fromCharCode(byte);
    return globalThis.btoa(binary);
  }

  public toString(): string {
    return "[opaque SyncV2ViewCheckpoint]";
  }
}

export type SyncV2ResultApplyOutcome =
  | "applied"
  | "duplicate"
  | "gap"
  | "reset-required";

export type SyncV2ResultResetReason =
  | "checkpoint-expired"
  | "retention-advanced"
  | "generation-changed"
  | "checkpoint-not-current"
  | "view-diverged"
  | "oversized"
  | "invalid-operations"
  | "invalid-checkpoint"
  | "backpressure";

export class SyncV2ViewError extends Error {
  public override readonly name = "SyncV2ViewError";
  public constructor(
    public readonly code: "offline" | "duplicate-identity" | "invalid-presentation" | "invalid_snapshot" | "invalid_checkpoint" | "oversized" | "unknown-identity" | "authoritative-gap" | "reset-required",
    reason: string,
  ) {
    super(`terrascale.sync.v2 view: ${code}: ${reason}`);
  }
}

export type SyncV2MutationStatus = "pending" | "accepted" | "rejected";

/**
 * Applies authorized snapshots and revisions atomically and rebases bounded
 * optimistic presentations over the latest authoritative result.
 */
export class SyncV2ResultViewStore {
  #items: SyncV2ResultItem[] = [];
  #viewGeneration = 0n;
  #authorizationGeneration = 0n;
  #revision = 0n;
  #digest = emptyResultDigest();
  #checkpoint: SyncV2ViewCheckpoint | undefined;
  #hasSnapshot = false;
  #online = true;
  readonly #optimistic = new Map<string, readonly SyncV2ResultOperation[]>();
  readonly #completed = new Set<string>();
  readonly #listeners = new Set<() => void>();

  public subscribe(listener: () => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  public getItems(): readonly SyncV2ResultItem[] {
    return freezeItems(this.#items);
  }

  public getVisibleItems(): readonly SyncV2ResultItem[] {
    let visible = cloneItems(this.#items);
    for (const operations of this.#optimistic.values()) {
      try {
        visible = applyOperations(visible, operations);
      } catch {
        return freezeItems(this.#items);
      }
    }
    return freezeItems(visible);
  }

  public getCheckpoint(): SyncV2ViewCheckpoint | undefined {
    return this.#checkpoint;
  }

  public getRevision(): bigint { return this.#revision; }
  public getViewGeneration(): bigint { return this.#viewGeneration; }
  public getAuthorizationGeneration(): bigint { return this.#authorizationGeneration; }
  public isOnline(): boolean { return this.#online; }

  public setOnline(online: boolean): void {
    if (this.#online === online) return;
    this.#online = online;
    this.#notify();
  }

  public bootstrap(snapshot: SyncV2ResultSnapshot): void {
    validateSnapshot(snapshot);
    this.#assertSnapshotAdvances(snapshot);
    this.#installSnapshot(snapshot);
  }

  public reset(snapshot: SyncV2ResultSnapshot): void {
    validateSnapshot(snapshot);
    this.#assertSnapshotAdvances(snapshot);
    this.#installSnapshot(snapshot);
  }

  /** Applies a complete revision atomically; duplicate revisions are safe. */
  public applyDelta(delta: SyncV2ResultDelta): SyncV2ResultApplyOutcome {
    if (delta.view_generation !== this.#viewGeneration) return "reset-required";
    if (delta.to_revision <= this.#revision) {
      return delta.to_revision === this.#revision && delta.digest !== this.#digest
        ? "reset-required"
        : "duplicate";
    }
    if (delta.from_revision !== this.#revision || delta.to_revision <= delta.from_revision) return "gap";
    if (delta.operations.length > syncV2ResultViewLimits.maxOperations) return "reset-required";
    let candidate: SyncV2ResultItem[];
    try {
      candidate = applyOperations(this.#items, delta.operations);
      validateItems(candidate);
    } catch {
      return "reset-required";
    }
    if (resultDigest(candidate) !== delta.digest) return "reset-required";
    if (!checkpointMatches(delta.checkpoint, delta.view_generation, delta.to_revision)) return "reset-required";
    this.#items = candidate;
    this.#revision = delta.to_revision;
    this.#digest = delta.digest;
    this.#checkpoint = delta.checkpoint;
    this.#notify();
    return "applied";
  }

  /** Applies policy loss before advancing to a new authorized generation. */
  public transitionAuthorization(
    authorizationGeneration: bigint,
    removals: readonly string[],
    replacement: SyncV2ResultSnapshot,
  ): void {
    if (authorizationGeneration <= this.#authorizationGeneration) {
      throw new SyncV2ViewError("reset-required", "authorization generation is stale");
    }
    validateSnapshot(replacement);
    if (replacement.authorization_generation !== authorizationGeneration
      || replacement.view_generation <= this.#viewGeneration) {
      throw new SyncV2ViewError("reset-required", "authorization and view generations disagree");
    }
    const removed = new Set(removals);
    if (replacement.items.some(item => removed.has(item.identity))) {
      throw new SyncV2ViewError(
        "reset-required",
        "the authorization replacement restores an identity declared removed",
      );
    }
    this.#assertSnapshotAdvances(replacement);
    this.#installSnapshot(replacement);
  }

  public beginMutation(identity: string, operations: readonly SyncV2ResultOperation[]): SyncV2MutationStatus {
    if (!this.#online) throw new SyncV2ViewError("offline", "durable mutations require an online session");
    validateIdentity(identity);
    if (this.#completed.has(identity) || this.#optimistic.has(identity)) {
      throw new SyncV2ViewError("duplicate-identity", "the idempotency identity is already known");
    }
    if (this.#optimistic.size >= maxPendingMutations || operations.length > syncV2ResultViewLimits.maxOperations) {
      throw new SyncV2ViewError("oversized", "the optimistic mutation bound was exceeded");
    }
    try {
      validateItems(applyOperations(this.#items, operations));
    } catch {
      throw new SyncV2ViewError("invalid-presentation", "the optimistic operations cannot apply");
    }
    this.#optimistic.set(identity, [...operations]);
    this.#notify();
    return "pending";
  }

  public acceptMutation(identity: string, delta: SyncV2ResultDelta): SyncV2MutationStatus {
    if (this.#completed.has(identity)) return "accepted";
    if (!this.#optimistic.has(identity)) throw new SyncV2ViewError("unknown-identity", "the mutation identity is not pending");
    const outcome = this.applyDelta(delta);
    if (outcome !== "applied" && outcome !== "duplicate") {
      throw new SyncV2ViewError(outcome === "gap" ? "authoritative-gap" : "reset-required", "the authoritative result needs recovery");
    }
    this.#optimistic.delete(identity);
    this.#rememberCompleted(identity);
    this.#notify();
    return "accepted";
  }

  public rejectMutation(identity: string): SyncV2MutationStatus {
    if (this.#completed.has(identity)) return "rejected";
    if (!this.#optimistic.delete(identity)) throw new SyncV2ViewError("unknown-identity", "the mutation identity is not pending");
    this.#rememberCompleted(identity);
    this.#notify();
    return "rejected";
  }

  #installSnapshot(snapshot: SyncV2ResultSnapshot): void {
    this.#items = cloneItems(snapshot.items);
    this.#viewGeneration = snapshot.view_generation;
    this.#authorizationGeneration = snapshot.authorization_generation;
    this.#revision = snapshot.revision;
    this.#digest = snapshot.digest;
    this.#checkpoint = snapshot.checkpoint;
    this.#hasSnapshot = true;
    this.#notify();
  }

  #assertSnapshotAdvances(snapshot: SyncV2ResultSnapshot): void {
    if (!this.#hasSnapshot) return;
    if (snapshot.view_generation < this.#viewGeneration
      || snapshot.authorization_generation < this.#authorizationGeneration
      || snapshot.revision < this.#revision) {
      throw new SyncV2ViewError(
        "reset-required",
        "the snapshot view, authorization, or revision generation regresses",
      );
    }
    if (snapshot.view_generation === this.#viewGeneration
      && snapshot.authorization_generation === this.#authorizationGeneration
      && snapshot.revision === this.#revision) {
      throw new SyncV2ViewError("reset-required", "the snapshot replays installed state");
    }
  }

  #rememberCompleted(identity: string): void {
    if (this.#completed.size >= maxPendingMutations) this.#completed.delete(this.#completed.values().next().value as string);
    this.#completed.add(identity);
  }

  #notify(): void {
    for (const listener of this.#listeners) listener();
  }
}

function validateIdentity(identity: string): void {
  if (identity.length === 0 || textEncoder.encode(identity).length > syncV2ResultViewLimits.maxIdentityBytes) {
    throw new SyncV2ViewError("invalid-presentation", "the result identity is outside its bound");
  }
}

function validateItems(items: readonly SyncV2ResultItem[]): void {
  if (items.length > syncV2ResultViewLimits.maxItems) throw new SyncV2ViewError("oversized", "the result item bound was exceeded");
  const identities = new Set<string>();
  for (const item of items) {
    validateIdentity(item.identity);
    if (identities.has(item.identity)) throw new SyncV2ViewError("invalid_snapshot", "result identities are not unique");
    identities.add(item.identity);
    let bytes = textEncoder.encode(item.identity).length + 16;
    for (const [path, value] of Object.entries(item.value)) {
      if (textEncoder.encode(path).length > syncV2ResultViewLimits.maxFieldPathBytes) throw new SyncV2ViewError("oversized", "a projected path exceeds its bound");
      if (!(value instanceof Uint8Array)) throw new SyncV2ViewError("invalid_snapshot", "projected values must be byte strings");
      bytes += textEncoder.encode(path).length + value.length + 16;
    }
    if (bytes > syncV2ResultViewLimits.maxValueBytes) throw new SyncV2ViewError("oversized", "a projected item exceeds its bound");
  }
}

function cloneItems(items: readonly SyncV2ResultItem[]): SyncV2ResultItem[] {
  return items.map(item => ({ identity: item.identity, value: Object.fromEntries(Object.entries(item.value).map(([key, value]) => [key, new Uint8Array(value)])) }));
}

function freezeItems(items: readonly SyncV2ResultItem[]): readonly SyncV2ResultItem[] {
  return Object.freeze(items.map(item => Object.freeze({
    identity: item.identity,
    value: Object.freeze(Object.fromEntries(Object.entries(item.value).map(([key, value]) => [key, new Uint8Array(value)]))),
  })));
}

function applyOperations(base: readonly SyncV2ResultItem[], operations: readonly SyncV2ResultOperation[]): SyncV2ResultItem[] {
  if (operations.length > syncV2ResultViewLimits.maxOperations) throw new SyncV2ViewError("oversized", "too many result operations");
  const items = cloneItems(base);
  for (const operation of operations) {
    const index = items.findIndex(item => item.identity === ("identity" in operation ? operation.identity : ""));
    switch (operation.kind) {
      case "Insert":
        validateIdentity(operation.identity);
        if (index !== -1 || operation.position < 0 || operation.position > items.length) throw new SyncV2ViewError("invalid_snapshot", "insert identity or position is invalid");
        items.splice(operation.position, 0, { identity: operation.identity, value: cloneValue(operation.value) });
        break;
      case "Update":
        if (index === -1) throw new SyncV2ViewError("invalid_snapshot", "update identity is absent");
        items[index] = { identity: operation.identity, value: cloneValue(operation.value) };
        break;
      case "Patch": {
        if (index === -1) throw new SyncV2ViewError("invalid_snapshot", "patch identity is absent");
        const value = { ...items[index]!.value };
        for (const change of operation.changes) {
          if (change.path.length === 0 || textEncoder.encode(change.path).length > syncV2ResultViewLimits.maxFieldPathBytes) throw new SyncV2ViewError("invalid_snapshot", "patch path is invalid");
          if (change.value === null) delete value[change.path];
          else value[change.path] = new Uint8Array(change.value);
        }
        items[index] = { identity: operation.identity, value };
        break;
      }
      case "Remove":
        if (index === -1) throw new SyncV2ViewError("invalid_snapshot", "remove identity is absent");
        items.splice(index, 1);
        break;
      case "Move":
        if (index === -1 || operation.position < 0 || operation.position >= items.length) throw new SyncV2ViewError("invalid_snapshot", "move identity or position is invalid");
        items.splice(operation.position, 0, ...items.splice(index, 1));
        break;
      case "Reset":
        validateSnapshot(operation.snapshot);
        items.splice(0, items.length, ...cloneItems(operation.snapshot.items));
        break;
    }
    validateItems(items);
  }
  return items;
}

function cloneValue(value: Readonly<Record<string, SyncV2ProjectedField>>): Readonly<Record<string, SyncV2ProjectedField>> {
  return Object.fromEntries(Object.entries(value).map(([key, bytes]) => [key, new Uint8Array(bytes)]));
}

function checkpointMatches(checkpoint: SyncV2ViewCheckpoint, generation: bigint, revision: bigint): boolean {
  // Metadata is carried by the enclosing snapshot/delta. The token itself is
  // deliberately opaque; non-empty and bounded is all the client can check.
  return checkpoint.toBytes().length > 0 && generation >= 0n && revision >= 0n;
}

function emptyResultDigest(): string {
  return "0".repeat(64);
}

/** Computes the same four-lane FNV-1a digest as the Rust result-view core. */
export function syncV2ResultDigest(items: readonly SyncV2ResultItem[]): string {
  validateItems(items);
  const bytes: number[] = [];
  for (const item of items) {
    pushU64(bytes, BigInt(textEncoder.encode(item.identity).length));
    bytes.push(...textEncoder.encode(item.identity));
    for (const key of Object.keys(item.value).sort(compareUtf8)) {
      const keyBytes = textEncoder.encode(key);
      const value = item.value[key]!;
      pushU64(bytes, BigInt(keyBytes.length));
      bytes.push(...keyBytes);
      pushU64(bytes, BigInt(value.length));
      bytes.push(...value);
    }
    bytes.push(0xff);
  }
  const digest = new Uint8Array(32);
  for (let lane = 0; lane < 4; lane++) {
    let hash = (0xcbf2_9ce4_8422_2325n ^ BigInt(lane) * 0x9e37_79b9_7f4a_7c15n) & maxU64;
    for (const byte of bytes) hash = ((hash ^ BigInt(byte)) * 0x0100_0000_01b3n) & maxU64;
    for (let index = 0; index < 8; index++) digest[lane * 8 + index] = Number((hash >> BigInt(index * 8)) & 0xffn);
  }
  return [...digest].map(byte => byte.toString(16).padStart(2, "0")).join("");
}

function compareUtf8(left: string, right: string): number {
  const a = textEncoder.encode(left);
  const b = textEncoder.encode(right);
  const count = Math.min(a.length, b.length);
  for (let index = 0; index < count; index++) {
    if (a[index]! !== b[index]!) return a[index]! - b[index]!;
  }
  return a.length - b.length;
}

function resultDigest(items: readonly SyncV2ResultItem[]): string {
  return syncV2ResultDigest(items);
}

function pushU64(bytes: number[], value: bigint): void {
  let rest = value & maxU64;
  for (let index = 0; index < 8; index++) {
    bytes.push(Number(rest & 0xffn));
    rest >>= 8n;
  }
}

function validateSnapshot(snapshot: SyncV2ResultSnapshot): void {
  if (snapshot.view_generation < 0n || snapshot.authorization_generation < 0n || snapshot.revision < 0n) throw new SyncV2ViewError("invalid_snapshot", "generation and revision must be unsigned");
  validateItems(snapshot.items);
  if (snapshot.digest !== resultDigest(snapshot.items)) throw new SyncV2ViewError("invalid_snapshot", "the result digest does not match");
  if (!checkpointMatches(snapshot.checkpoint, snapshot.view_generation, snapshot.revision)) throw new SyncV2ViewError("invalid_checkpoint", "the checkpoint does not match the result");
}
