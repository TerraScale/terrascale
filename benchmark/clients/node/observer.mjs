// Benchmark-only transport observation shared by the raw and SDK lanes.
import { subscribe } from "node:diagnostics_channel";
import { createTsAuthClient } from "@terrascale/sdk/ts-auth";
const HOST = "api.terrascale.tech";
const AUTH_ORIGIN = "https://auth.terrascale.tech";
const AUTH_SLUG = "terrascale";
const PIN_HEADER = "X-TerraScale-Region-Pin";
const REGION_HEADER = "x-terrabase-region";

// Cells run one at a time, so one observer is live at a time; undici publishes these process-wide.
let active = null;
export function setActive(observer) { active = observer; }
subscribe("undici:client:connected", ({ connectParams }) => {
  if (active && connectParams.hostname === HOST && Number(connectParams.port || 443) === 443) active.connects += 1;
});
subscribe("undici:client:sendHeaders", ({ request, socket }) => {
  const observer = active;
  if (!observer?.measured || !observer.pinned(new URL(request.path, String(request.origin)), request.method)) return;
  const version = socket?.alpnProtocol === "h2" ? "HTTP/2" : "HTTP/1.1";
  observer.sent += 1;
  observer.versions[version] = (observer.versions[version] ?? 0) + 1;
  if (observer.protocol !== undefined && version !== observer.protocol) observer.protocolBad += 1;
});

/** Counts every pinned attempt, including SDK retries, and validates each response. */
export class Observer {
  connects = 0; // TCP connects to the API host, readiness included.
  constructor(routes, region, protocol = undefined, measured = true) {
    Object.assign(this, { routes, region, protocol, measured });
    this.window();
  }
  window() {
    this.attempts = this.good = this.sent = this.protocolBad = 0;
    this.versions = {};
  }
  pinned(url, method) {
    return url.protocol === "https:" && url.hostname === HOST && (url.port === "" || url.port === "443")
      && this.routes.has(`${method} ${url.pathname}`);
  }
  /** The fetch given to the SDK's public `fetch` option and used by raw lanes; pins exact routes only. */
  observe = async (input, init = {}) => {
    const url = new URL(String(input));
    if (!this.pinned(url, init.method ?? "GET")) return fetch(url, init);
    const headers = new Headers(init.headers);
    headers.set(PIN_HEADER, this.region);
    if (this.measured) this.attempts += 1;
    const response = await fetch(url, { ...init, headers });
    if (this.measured && response.status === 200 && response.headers.get(REGION_HEADER) === this.region) this.good += 1;
    return response;
  };
}

/** One cached source per lane, refreshed before expiry; the JS SDK has no built-in key exchange. */
export function tokenSource(key, project) {
  const auth = createTsAuthClient({ apiBaseUrl: AUTH_ORIGIN, instanceSlug: AUTH_SLUG });
  let cached;
  let pending;
  const exchange = async (signal) => {
    const started = Date.now();
    const result = await auth.oidc.token(key.slice(0, 26), key, { grant_type: "client_credentials", project },
      { signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(60_000)]) : AbortSignal.timeout(60_000) });
    const lifetime = result.ok ? result.value.expires_in : NaN;
    if (!result.ok || !Number.isFinite(lifetime) || lifetime <= 0) throw new Error("token exchange failed");
    cached = { token: result.value.access_token, refreshAt: started + (lifetime - Math.min(60, lifetime / 2)) * 1000 };
    return cached.token;
  };
  return async (signal) => {
    signal?.throwIfAborted();
    if (cached && Date.now() < cached.refreshAt) return cached.token;
    pending ??= exchange(signal).finally(() => { pending = undefined; });
    if (!signal) return pending;
    let abort;
    const cancelled = new Promise((_, reject) => {
      abort = () => reject(signal.reason);
      signal.addEventListener("abort", abort, { once: true });
    });
    try { return await Promise.race([pending, cancelled]); }
    finally { signal.removeEventListener("abort", abort); }
  };
}

export function cellRejection(ready, stats, observer) {
  if (!ready) return "readiness";
  if (stats.failed) return "operation_error";
  if (stats.stuck) return "drain_timeout";
  if (observer.attempts !== stats.started || observer.attempts !== observer.good || observer.sent !== observer.attempts || observer.protocolBad) return "attempt_mismatch";
  return stats.latencies.length ? null : "empty";
}
