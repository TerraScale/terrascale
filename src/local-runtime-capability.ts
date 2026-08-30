import type { FetchLike } from "./config.js";

const localRuntimeFetches = new WeakSet<FetchLike>();

/**
 * Marks the in-process transport owned by one explicit local runtime.
 *
 * This module is intentionally not re-exported by the package root. The
 * marker prevents ordinary customer configuration from imitating the local
 * runtime with a matching URL and an arbitrary fetch function.
 */
export function registerLocalRuntimeFetch(fetch: FetchLike): void {
  localRuntimeFetches.add(fetch);
}

export function isRegisteredLocalRuntimeFetch(fetch: FetchLike | undefined): boolean {
  return fetch !== undefined && localRuntimeFetches.has(fetch);
}
