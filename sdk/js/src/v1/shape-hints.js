import { object, string } from "./decode.js";
import { TerraBaseV1Error } from "./errors.js";
import { parseExactJson } from "./json.js";

export const shapeHintProtocol = "terrascale.shape.v1";
/** @typedef {{ handle: string; offset: string; next_offset: string; manifest: import("./json.js").JsonObject; local?: string }} Hint */

export class ShapeHints {
  /** @type {import("./sql-socket.js").SqlSocketLike} */
  #socket;
  /** @type {Hint | undefined} */
  #latest;
  /** @type {{ handle: string; offset: string; local: string } | undefined} */
  #provisional;
  /** @type {Error | undefined} */
  #error;
  /** @type {(() => void) | undefined} */
  #wake;
  /** @param {import("./sql-socket.js").SqlSocketLike} socket */
  constructor(socket) { this.#socket = socket; }

  /** @param {import("./sql-socket.js").SqlSocketLike} socket @param {AbortSignal} signal @param {string | undefined} token */
  static async open(socket, signal, token) {
    const session = new ShapeHints(socket);
    let ready = false;
    let resolveReady = () => {};
    /** @type {(error: unknown) => void} */
    let rejectReady = () => {};
    const connected = new Promise((resolve, reject) => { resolveReady = () => resolve(undefined); rejectReady = reject; });
    const fail = (/** @type {Error} */ error) => { session.#error ??= error; session.#wake?.(); rejectReady(session.#error); };
    const abort = () => { session.close(); fail(signal.reason); };
    const deadline = setTimeout(() => { fail(new Error("Shape hint connection timed out.")); session.close(); }, 10_000);
    signal.addEventListener("abort", abort, { once: true });
    socket.addEventListener("open", () => {
      if (socket.protocol !== shapeHintProtocol) { fail(new TypeError("Invalid shape hint subprotocol.")); session.close(); return; }
      if (token !== undefined) socket.send(JSON.stringify({ type: "auth", token }));
    }, { once: true });
    socket.addEventListener("error", () => fail(new Error("Shape hint connection failed.")));
    socket.addEventListener("close", () => { signal.removeEventListener("abort", abort); fail(new Error("Shape hint connection closed.")); });
    socket.addEventListener("message", event => {
      try {
        if (typeof event.data !== "string" || event.data.length > 65536) throw new TypeError("Invalid shape hint frame.");
        const frame = object(parseExactJson(event.data), "shape hint");
        if (frame.type === "error") throw new TerraBaseV1Error({ status: frame.code === "shape_busy" ? 429 : frame.code === "invalid_key" ? 401 : frame.code === "must_refetch" ? 409 : 503,
          code: string(frame.code, "shape hint error"), ...(typeof frame.retry_after === "number" && Number.isFinite(frame.retry_after) && frame.retry_after >= 0 ? { retryAfter: frame.retry_after } : {}) });
        if (!ready) {
          if (frame.type !== "ready") throw new TypeError("Expected shape hint ready frame.");
          ready = true; resolveReady(); return;
        }
        const handle = string(frame.handle, "shape hint handle");
        const offset = string(frame.offset, "shape hint offset");
        if (frame.type === "provisional") {
          const local = string(frame.local, "shape hint local");
          if (!/^h1-[a-f0-9]{16}$/u.test(handle) || !/^(0|[1-9]\d*)$/u.test(offset) || !/^(?:0|[a-f0-9]{16})$/u.test(local)) throw new TypeError("Invalid shape hint boundary.");
          session.#provisional = { handle, offset, local };
          session.#wake?.();
          return;
        }
        const next = string(frame.next_offset, "shape hint next offset");
        if (frame.type !== "new_data" || !/^h1-[a-f0-9]{16}$/u.test(handle) || !/^(0|[1-9]\d*)$/u.test(offset)
          || !/^(0|[1-9]\d*)$/u.test(next) || BigInt(next) <= BigInt(offset)) throw new TypeError("Invalid shape hint boundary.");
        session.#latest = { handle, offset, next_offset: next, manifest: object(frame.manifest, "shape hint manifest") };
        session.#wake?.();
      } catch (error) { fail(error instanceof Error ? error : new Error("Invalid shape hint.")); session.close(); }
    });
    try {
      signal.throwIfAborted();
      await connected;
      return session;
    } catch (error) { session.close(); throw error; }
    finally { clearTimeout(deadline); }
  }

  /**
   * Resolves with the next hint past `offset`, or with a hint carrying `local` when the region's pending local writes no longer match `local`.
   * @param {string} handle @param {string} offset @param {string} local @param {AbortSignal} signal @returns {Promise<Hint>}
   */
  async wait(handle, offset, local, signal) {
    while (true) {
      signal.throwIfAborted();
      if (this.#error !== undefined) throw this.#error;
      if (this.#latest !== undefined) {
        if (this.#latest.handle !== handle) throw new TerraBaseV1Error({ status: 409, code: "must_refetch" });
        if (BigInt(this.#latest.next_offset) > BigInt(offset)) return this.#latest;
      }
      const provisional = this.#provisional;
      if (provisional !== undefined && provisional.handle === handle && provisional.offset === offset && provisional.local !== local)
        return { handle, offset, next_offset: offset, manifest: {}, local: provisional.local };
      await new Promise(resolve => { this.#wake = () => resolve(undefined); });
      this.#wake = undefined;
    }
  }

  close() { this.#error ??= new Error("Shape hints closed."); this.#socket.close(1000, ""); this.#wake?.(); }
}
