const AUTH_DISCOVERY_URL = "https://atlas.terrascale.tech/api/v1/auth/discovery";
const MAX_ENDPOINTS = 20;
const MAX_BYTES = 65_536;

/** The public auth routing document, validated before any credentials are sent. */
export class TsAuthEndpointDiscovery {
  readonly #fetch: typeof globalThis.fetch;
  #cached: { readonly endpoints: readonly string[]; readonly expiresAt: number } | undefined;

  constructor(fetchHandler: typeof globalThis.fetch) {
    this.#fetch = fetchHandler;
  }

  async endpoints(signal?: AbortSignal): Promise<readonly string[]> {
    if (this.#cached !== undefined && this.#cached.expiresAt > Date.now()) return this.#cached.endpoints;
    const deadline = AbortSignal.timeout(3_000);
    const discoverySignal = signal === undefined ? deadline : AbortSignal.any([signal, deadline]);
    const response = await this.#fetch(new Request(AUTH_DISCOVERY_URL, {
      redirect: "manual", signal: discoverySignal,
    }));
    if (!response.ok) throw new TypeError(`Atlas auth discovery returned HTTP ${response.status}.`);
    if (response.headers.get("content-length") !== null && Number(response.headers.get("content-length")) > MAX_BYTES) {
      throw new TypeError("Atlas auth discovery response is too large.");
    }
    const body = await response.text();
    if (body.length > MAX_BYTES) throw new TypeError("Atlas auth discovery response is too large.");
    let payload: unknown;
    try { payload = JSON.parse(body); } catch { throw new TypeError("Atlas auth discovery response is invalid JSON."); }
    if (typeof payload !== "object" || payload === null || Array.isArray(payload)) throw new TypeError("Atlas auth discovery response is invalid.");
    const doc = payload as Record<string, unknown>;
    if (doc.v !== 1 || !Number.isInteger(doc.refreshSeconds) || (doc.refreshSeconds as number) < 1 ||
        (doc.refreshSeconds as number) > 3600 || !Array.isArray(doc.endpoints) ||
        doc.endpoints.length < 1 || doc.endpoints.length > MAX_ENDPOINTS) {
      throw new TypeError("Atlas auth discovery response is invalid.");
    }
    const seen = new Set<string>();
    const endpoints: string[] = [];
    for (const entry of doc.endpoints) {
      if (typeof entry !== "object" || entry === null || Array.isArray(entry)) throw new TypeError("Atlas auth endpoint is invalid.");
      const { region, url } = entry as Record<string, unknown>;
      if (typeof region !== "string" || !/^[A-Z0-9][A-Z0-9-]{0,15}$/u.test(region) || seen.has(region) || typeof url !== "string") {
        throw new TypeError("Atlas auth endpoint is invalid.");
      }
      let parsed: URL;
      try { parsed = new URL(url); } catch { throw new TypeError("Atlas auth endpoint URL is invalid."); }
      if (parsed.protocol !== "https:" || parsed.hostname !== `${region.toLowerCase()}.auth.terrascale.tech` ||
          parsed.port !== "" || parsed.username !== "" || parsed.password !== "" || parsed.pathname !== "/" ||
          parsed.search !== "" || parsed.hash !== "") {
        throw new TypeError("Atlas auth endpoint URL does not match its region.");
      }
      seen.add(region);
      endpoints.push(parsed.origin);
    }
    this.#cached = { endpoints, expiresAt: Date.now() + Math.min(doc.refreshSeconds as number, 60) * 1000 };
    return endpoints;
  }
}
