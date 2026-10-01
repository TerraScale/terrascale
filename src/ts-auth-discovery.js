const AUTH_DISCOVERY_URL =
  "https://atlas.terrascale.tech/api/v1/auth/discovery";
const MAX_ENDPOINTS = 20;
const MAX_BYTES = 65_536;

/** The public auth routing document, validated before any credentials are sent. */
export class TsAuthEndpointDiscovery {
  /**
   * @type {typeof globalThis.fetch}
   * @readonly
   */
  #fetch;
  /** @type {{ readonly endpoints: readonly string[]; readonly expiresAt: number } | undefined} */
  #cached;

  /** @param {typeof globalThis.fetch} fetchHandler */
  constructor(fetchHandler) {
    this.#fetch = fetchHandler;
  }

  /**
   * @param {AbortSignal} [signal]
   * @returns {Promise<readonly string[]>}
   */
  async endpoints(signal) {
    if (this.#cached !== undefined && this.#cached.expiresAt > Date.now())
      return this.#cached.endpoints;
    const deadline = AbortSignal.timeout(3_000);
    const discoverySignal =
      signal === undefined ? deadline : AbortSignal.any([signal, deadline]);
    const response = await awaitAbortable(
      this.#fetch(
        new Request(AUTH_DISCOVERY_URL, {
          redirect: "manual",
          signal: discoverySignal,
        }),
      ).then((response) => {
        if (discoverySignal.aborted)
          void response.body?.cancel().catch(() => undefined);
        return response;
      }),
      discoverySignal,
    );
    if (!response.ok) {
      void response.body?.cancel().catch(() => undefined);
      throw new TypeError(
        `Atlas auth discovery returned HTTP ${response.status}.`,
      );
    }
    const body = await readBoundedBody(response, discoverySignal);
    /** @type {unknown} */
    let payload;
    try {
      payload = JSON.parse(body);
    } catch {
      throw new TypeError("Atlas auth discovery response is invalid JSON.");
    }
    if (
      typeof payload !== "object" ||
      payload === null ||
      Array.isArray(payload)
    )
      throw new TypeError("Atlas auth discovery response is invalid.");
    const doc = /** @type {Record<string, unknown>} */ (payload);
    if (
      doc.v !== 1 ||
      !Number.isInteger(doc.refreshSeconds) ||
      /** @type {number} */ (doc.refreshSeconds) < 1 ||
      /** @type {number} */ (doc.refreshSeconds) > 3600 ||
      !Array.isArray(doc.endpoints) ||
      doc.endpoints.length < 1 ||
      doc.endpoints.length > MAX_ENDPOINTS
    ) {
      throw new TypeError("Atlas auth discovery response is invalid.");
    }
    /** @type {Set<string>} */
    const seen = new Set();
    /** @type {string[]} */
    const endpoints = [];
    for (const entry of doc.endpoints) {
      if (typeof entry !== "object" || entry === null || Array.isArray(entry))
        throw new TypeError("Atlas auth endpoint is invalid.");
      const { region, url } = /** @type {Record<string, unknown>} */ (entry);
      if (
        typeof region !== "string" ||
        !/^[A-Z0-9][A-Z0-9-]{0,15}$/u.test(region) ||
        seen.has(region) ||
        typeof url !== "string"
      ) {
        throw new TypeError("Atlas auth endpoint is invalid.");
      }
      /** @type {URL} */
      let parsed;
      try {
        parsed = new URL(url);
      } catch {
        throw new TypeError("Atlas auth endpoint URL is invalid.");
      }
      if (
        parsed.protocol !== "https:" ||
        parsed.hostname !== `${region.toLowerCase()}.auth.terrascale.tech` ||
        parsed.port !== "" ||
        parsed.username !== "" ||
        parsed.password !== "" ||
        parsed.pathname !== "/" ||
        parsed.search !== "" ||
        parsed.hash !== ""
      ) {
        throw new TypeError(
          "Atlas auth endpoint URL does not match its region.",
        );
      }
      seen.add(region);
      endpoints.push(parsed.origin);
    }
    this.#cached = {
      endpoints,
      expiresAt:
        Date.now() +
        Math.min(/** @type {number} */ (doc.refreshSeconds), 60) * 1000,
    };
    return endpoints;
  }
}

/**
 * @param {Response} response
 * @param {AbortSignal} signal
 * @returns {Promise<string>}
 */
async function readBoundedBody(response, signal) {
  if (Number(response.headers.get("content-length")) > MAX_BYTES) {
    void response.body?.cancel().catch(() => undefined);
    throw new TypeError("Atlas auth discovery response is too large.");
  }
  const reader = response.body?.getReader();
  if (reader === undefined)
    throw new TypeError("Atlas auth discovery response is empty.");
  /** @type {Uint8Array[]} */
  const parts = [];
  let size = 0;
  try {
    while (true) {
      const chunk = await awaitAbortable(reader.read(), signal);
      if (chunk.done) break;
      size += chunk.value.byteLength;
      if (size > MAX_BYTES)
        throw new TypeError("Atlas auth discovery response is too large.");
      parts.push(chunk.value);
    }
  } catch (error) {
    void reader.cancel().catch(() => undefined);
    throw error;
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const part of parts) {
    bytes.set(part, offset);
    offset += part.byteLength;
  }
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
}

/**
 * @template T
 * @param {Promise<T>} work
 * @param {AbortSignal} signal
 * @returns {Promise<T>}
 */
async function awaitAbortable(work, signal) {
  if (signal.aborted) {
    void work.catch(() => undefined);
    throw signal.reason;
  }
  /** @type {() => void} */
  let onAbort = () => undefined;
  /** @type {Promise<never>} */
  const aborted = new Promise((_, reject) => {
    onAbort = () => reject(signal.reason);
    signal.addEventListener("abort", onAbort, { once: true });
    if (signal.aborted) onAbort();
  });
  try {
    return await Promise.race([work, aborted]);
  } finally {
    signal.removeEventListener("abort", onAbort);
  }
}
