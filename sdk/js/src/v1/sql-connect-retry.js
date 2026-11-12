import { parseV1Error, TerraBaseV1Error } from "./errors.js";

const maximumAttempts = 3;
const delayCapSeconds = 30;

/**
 * @template T
 * @param {(signal: AbortSignal) => Promise<T>} open
 * @param {number} timeout
 * @param {AbortSignal | undefined} signal
 * @returns {Promise<T>}
 */
export async function retrySqlConnect(open, timeout, signal) {
  const expiresAt = Date.now() + timeout;
  const controller = new AbortController();
  const bounded = signal === undefined ? controller.signal : AbortSignal.any([signal, controller.signal]);
  const timer = setTimeout(() => controller.abort(new DOMException("SQL connection deadline expired.", "TimeoutError")), timeout);
  try {
    for (let attempt = 1; ; attempt++) {
      bounded.throwIfAborted();
      try {
        return await open(bounded);
      } catch (error) {
        bounded.throwIfAborted();
        if (!(error instanceof TerraBaseV1Error) || ![429, 503].includes(error.status)
          || error.retryAfter === undefined || attempt >= maximumAttempts) throw error;
        const delay = Math.min(error.retryAfter, delayCapSeconds) * 1000;
        if (delay >= expiresAt - Date.now()) throw error;
        await wait(delay, bounded);
      }
    }
  } finally {
    clearTimeout(timer);
  }
}

/** @param {number} milliseconds @param {AbortSignal | undefined} signal @returns {Promise<void>} */
function wait(milliseconds, signal) {
  signal?.throwIfAborted();
  return new Promise((resolve, reject) => {
    const abort = () => { clearTimeout(timer); reject(signal?.reason); };
    const timer = setTimeout(() => { signal?.removeEventListener("abort", abort); resolve(); }, milliseconds);
    signal?.addEventListener("abort", abort, { once: true });
  });
}

/** @param {unknown} socket @param {(error: unknown) => void} reject */
export function listenForSqlRefusal(socket, reject) {
  const node = /** @type {any} */ (socket);
  if (typeof node.on !== "function") return;
  node.on("unexpected-response", (/** @type {unknown} */ _request, /** @type {any} */ response) => {
    const headers = new Headers();
    for (const [name, value] of Object.entries(response.headers)) {
      if (typeof value === "string") headers.set(name, value);
      else if (Array.isArray(value)) for (const item of value) headers.append(name, item);
    }
    let text = "";
    response.setEncoding("utf8");
    response.on("data", (/** @type {string} */ chunk) => {
      if (text.length < 65536) text += chunk.slice(0, 65536 - text.length);
    });
    response.on("end", () => {
      const refusal = parseV1Error(response.statusCode, headers, text);
      const seconds = headers.get("retry-after")?.trim();
      if (seconds !== undefined && /^\d+$/u.test(seconds)) refusal.retryAfter = Number(seconds);
      reject(refusal);
      node.close();
    });
    response.on("error", (/** @type {unknown} */ error) => { reject(error); node.close(); });
  });
}
