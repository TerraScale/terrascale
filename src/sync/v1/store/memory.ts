import type { SyncV1StoredViewState, SyncV1ViewStore } from "./index.js";

/** The volatile in-memory view store; useful as a fallback and in tests. */
export class SyncV1MemoryViewStore implements SyncV1ViewStore {
  readonly #views = new Map<string, SyncV1StoredViewState>();

  public load(view: string): Promise<SyncV1StoredViewState | undefined> {
    return Promise.resolve(this.#views.get(view));
  }

  public async save(view: string, state: SyncV1StoredViewState): Promise<void> {
    // Stored states are treated as immutable; every capture builds a fresh one.
    this.#views.set(view, state);
  }

  public async clear(view: string): Promise<void> {
    this.#views.delete(view);
  }
}
