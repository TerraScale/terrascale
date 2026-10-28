import { Agent } from "undici";

/** @type {Agent | undefined} */
let dispatcher;

/** Capture the current fetch while sharing one lazy connection pool across clients.
 * @returns {typeof globalThis.fetch | undefined}
 */
export function defaultFetch() {
  const fetch = globalThis.fetch?.bind(globalThis);
  if (fetch === undefined) return undefined;
  return (input, init) => {
    dispatcher ??= new Agent({ allowH2: true });
    const options = { ...init, dispatcher };
    return fetch(input, options);
  };
}
