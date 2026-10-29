// Browser storage for a shape subscription's read replica. Durable writes never queue here.
/** @import { V1ShapeReplicaStore } from "./shape-subscription.js" */

/**
 * A V1ShapeReplicaStore kept in browser IndexedDB, one record per key.
 * @param {string} key Names one subject's shape, e.g. `${userId}/notes/open`; clear it on sign-out.
 * @returns {V1ShapeReplicaStore}
 */
export function indexedDbShapeReplica(key) {
  if (typeof key !== "string" || key === "") throw new TypeError("An IndexedDB replica key is a non-empty string.");
  /** @type {Promise<IDBDatabase> | undefined} */
  let opened;
  /** @returns {Promise<IDBDatabase>} */
  const open = () => opened ??= new Promise((resolve, reject) => {
    const request = indexedDB.open("terrascale-shape-replicas", 1);
    request.onupgradeneeded = () => { request.result.createObjectStore("replicas"); };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => { opened = undefined; reject(request.error); };
  });
  /**
   * @template T
   * @param {IDBTransactionMode} mode
   * @param {(store: IDBObjectStore) => IDBRequest<T>} operation
   * @returns {Promise<T>}
   */
  const run = async (mode, operation) => {
    const database = await open();
    return new Promise((resolve, reject) => {
      const transaction = database.transaction("replicas", mode);
      const request = operation(transaction.objectStore("replicas"));
      transaction.oncomplete = () => resolve(request.result);
      transaction.onerror = transaction.onabort = () => reject(transaction.error ?? request.error);
    });
  };
  return Object.freeze({
    load: async () => {
      const value = await run("readonly", store => store.get(key));
      return typeof value === "string" ? value : undefined;
    },
    /** @param {string | undefined} text */
    save: async text => {
      if (text === undefined) await run("readwrite", store => store.delete(key));
      else await run("readwrite", store => store.put(text, key));
    },
  });
}
