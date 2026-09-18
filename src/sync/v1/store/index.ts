/**
 * Durable local materialization for reactive views: free local reads.
 *
 * A view store persists the materialized authorized result plus the last
 * opaque server checkpoint. Reads are served from that local copy with no
 * network round-trip; attach resumes from the persisted checkpoint and the
 * server issues a fresh authorized cut (reset) when it has expired. The
 * stored state is JSON-safe so every implementation stays a dumb passthrough.
 * Durable client writes are never persisted here.
 */

import {
  SyncV1ViewCheckpoint,
  syncV1ResultDigest,
  type SyncV1ResultSnapshot,
  type SyncV1ResultViewStore,
} from "../result-view.js";

export { SyncV1MemoryViewStore } from "./memory.js";
export { createSyncV1BrowserViewStore } from "./indexeddb.js";

/** One projected field, base64-encoded so the state survives any serializer. */
export type SyncV1StoredViewField = {
  readonly path: string;
  readonly value: string;
};

/** One authorized result row in its durable form. */
export type SyncV1StoredViewItem = {
  readonly identity: string;
  readonly fields: readonly SyncV1StoredViewField[];
};

/** The JSON-safe durable snapshot of one materialized view. */
export type SyncV1StoredViewState = {
  readonly query: string;
  readonly parameters: string;
  readonly view_generation: string;
  readonly authorization_generation: string;
  readonly revision: string;
  readonly items: readonly SyncV1StoredViewItem[];
  readonly digest: string;
  /** The opaque server token, persisted verbatim. */
  readonly checkpoint: string;
};

/** Async durable storage keyed by the stable view identity. */
export type SyncV1ViewStore = {
  load(view: string): Promise<SyncV1StoredViewState | undefined>;
  save(view: string, state: SyncV1StoredViewState): Promise<void>;
  clear(view: string): Promise<void>;
};

/** The durable identity of one view: its subscription id. */
export function syncV1ViewStoreKey(options: { readonly subscription: string }): string {
  return options.subscription;
}

/** True when the stored state belongs to the query being subscribed. */
export function syncV1StoredViewStateMatches(
  state: SyncV1StoredViewState,
  query: string,
  parameters: Uint8Array,
): boolean {
  if (state.query !== query) return false;
  try {
    return bytesEqual(base64ToBytes(state.parameters), parameters);
  } catch {
    return false;
  }
}

/** Type guard for states loaded from untrusted durability such as disk. */
export function isSyncV1StoredViewState(value: unknown): value is SyncV1StoredViewState {
  return isRecord(value)
    && typeof value.query === "string"
    && typeof value.parameters === "string"
    && typeof value.view_generation === "string"
    && typeof value.authorization_generation === "string"
    && typeof value.revision === "string"
    && typeof value.digest === "string"
    && typeof value.checkpoint === "string"
    && Array.isArray(value.items)
    && value.items.every(item => isRecord(item)
      && typeof item.identity === "string"
      && Array.isArray(item.fields)
      && item.fields.every(field => isRecord(field)
        && typeof field.path === "string"
        && typeof field.value === "string"));
}

/** Decodes a stored state into a bootstrap snapshot; undefined when corrupt. */
export function syncV1StoredViewToSnapshot(state: SyncV1StoredViewState): SyncV1ResultSnapshot | undefined {
  try {
    return {
      view_generation: BigInt(state.view_generation),
      authorization_generation: BigInt(state.authorization_generation),
      revision: BigInt(state.revision),
      items: state.items.map(item => ({
        identity: item.identity,
        value: storedValue(item.fields),
      })),
      digest: state.digest,
      checkpoint: new SyncV1ViewCheckpoint(base64ToBytes(state.checkpoint)),
    };
  } catch {
    return undefined;
  }
}

/** Captures the current materialized view state; undefined before the first snapshot. */
export function syncV1CaptureViewState(
  view: Pick<SyncV1ResultViewStore,
    "getItems" | "getCheckpoint" | "getRevision" | "getViewGeneration" | "getAuthorizationGeneration"
  >,
  query: string,
  parameters: Uint8Array,
): SyncV1StoredViewState | undefined {
  const checkpoint = view.getCheckpoint();
  if (checkpoint === undefined) return undefined;
  const items = view.getItems();
  return {
    query,
    parameters: bytesToBase64(parameters),
    view_generation: view.getViewGeneration().toString(10),
    authorization_generation: view.getAuthorizationGeneration().toString(10),
    revision: view.getRevision().toString(10),
    items: items.map(item => ({
      identity: item.identity,
      fields: Object.keys(item.value).sort().map(path => ({ path, value: bytesToBase64(item.value[path]!) })),
    })),
    digest: syncV1ResultDigest(items),
    checkpoint: bytesToBase64(checkpoint.toBytes()),
  };
}

/** The identity of one durable state write; equal signatures need no rewrite. */
export function syncV1StoredViewStateSignature(state: SyncV1StoredViewState): string {
  return `${state.view_generation}:${state.authorization_generation}:${state.revision}:${state.digest}`;
}

function storedValue(fields: readonly SyncV1StoredViewField[]): Record<string, Uint8Array> {
  const value: Record<string, Uint8Array> = Object.create(null) as Record<string, Uint8Array>;
  for (const field of fields) value[field.path] = base64ToBytes(field.value);
  return value;
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let offset = 0; offset < bytes.length; offset += chunk) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk));
  }
  return globalThis.btoa(binary);
}

function base64ToBytes(value: string): Uint8Array {
  const binary = globalThis.atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  return left.length === right.length && left.every((byte, index) => byte === right[index]);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
