import { SyncV1MemoryViewStore } from "./memory.js";
import { isSyncV1StoredViewState, type SyncV1StoredViewState, type SyncV1ViewStore } from "./index.js";

/**
 * Opens the browser IndexedDB view store. Falls back to an in-memory store
 * when IndexedDB is unavailable or cannot be opened; a failed read or write
 * never blocks live delivery.
 */
export async function createSyncV1BrowserViewStore(name = "terrascale.sync.v1"): Promise<SyncV1ViewStore> {
  const fallback = new SyncV1MemoryViewStore();
  try {
    const factory = globalThis.indexedDB;
    if (factory === undefined) return fallback;
    const database = await new Promise<IDBDatabase>((resolve, reject) => {
      const request = factory.open(name, 1);
      request.onupgradeneeded = () => {
        if (!request.result.objectStoreNames.contains("views")) request.result.createObjectStore("views");
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error ?? new Error("the IndexedDB open request failed"));
      request.onblocked = () => reject(new Error("the IndexedDB open request was blocked"));
    });
    const run = <T>(mode: IDBTransactionMode, operate: (views: IDBObjectStore) => IDBRequest<T>): Promise<T> =>
      new Promise<T>((resolve, reject) => {
        const transaction = database.transaction("views", mode);
        const request = operate(transaction.objectStore("views"));
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error ?? new Error("the IndexedDB request failed"));
        transaction.onabort = () => reject(transaction.error ?? new Error("the IndexedDB transaction aborted"));
      });
    return {
      load: async (view: string): Promise<SyncV1StoredViewState | undefined> => {
        try {
          const value: unknown = await run("readonly", store => store.get(view) as IDBRequest<unknown>);
          return isSyncV1StoredViewState(value) ? value : undefined;
        } catch {
          return undefined;
        }
      },
      save: async (view: string, state: SyncV1StoredViewState): Promise<void> => {
        try {
          await run("readwrite", store => store.put(state, view));
        } catch {
          // A failed durable write never blocks the live view.
        }
      },
      clear: async (view: string): Promise<void> => {
        try {
          await run("readwrite", store => store.delete(view));
        } catch {
          // Nothing durable is recoverable when the delete fails.
        }
      },
    };
  } catch {
    return fallback;
  }
}
