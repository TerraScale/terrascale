import { err, ok } from "./result.js";
import { requireId, isValidId } from "./ids.js";
import { parseStrictJson } from "./strict-json.js";
import { TsAuthHttp } from "./http.js";

/** @typedef {{agentId: string, kid: string, key: CryptoKey}} AgentIdentity */
/** @typedef {{access_token: string, expires_in: number, token_type: "Bearer", scope: string}} AgentToken */
/** @typedef {{isAgent: true, agentId: string, ownerId: string, actingFor: "owner" | "self", scopes: string[]}} VerifiedAgent */
/** @typedef {{enrollment_id: string, user_code: string, verification_uri: string, verification_uri_complete: string, expires_in: number, interval: number}} Enrollment */
/** @import { TerraScaleResult } from "./result.js" */

/** @param {Uint8Array} bytes */
function base64url(bytes) {
  return btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}
/** @param {string} text */
function decode(text) {
  if (!/^[A-Za-z0-9_-]+$/u.test(text)) throw new TypeError("Invalid JWT encoding.");
  return Uint8Array.from(atob(text.replaceAll("-", "+").replaceAll("_", "/")), c => c.charCodeAt(0));
}
/** @param {object} value */
function part(value) { return base64url(new TextEncoder().encode(JSON.stringify(value))); }
/** @param {CryptoKey} key @param {string} aud @param {Record<string, string>} [claims] @param {number} [iat] */
async function assertion(key, aud, claims = {}, iat = Math.floor(Date.now() / 1000)) {
  const { kid, ...identity } = claims;
  const input = `${part({ alg: "ES256", typ: "JWT", ...(kid === undefined ? {} : { kid }) })}.${part({ ...identity, aud, iat, exp: iat + 120, jti: crypto.randomUUID() })}`;
  const signature = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, new TextEncoder().encode(input));
  return `${input}.${base64url(new Uint8Array(signature))}`;
}
/** @param {number} ms @param {AbortSignal} [signal] @returns {Promise<void>} */
function waitInterval(ms, signal) {
  return new Promise((resolve, reject) => {
    signal?.throwIfAborted();
    const abort = () => { clearTimeout(timer); reject(signal?.reason); };
    const timer = setTimeout(() => { signal?.removeEventListener("abort", abort); resolve(); }, ms);
    signal?.addEventListener("abort", abort, { once: true });
  });
}

export class TsAuthAgentsClient {
  /** @type {TsAuthHttp} */
  #http;
  /** @type {WeakMap<CryptoKey, Map<string, {token: AgentToken, expires: number}>>} */
  #tokens = new WeakMap();
  /** @param {TsAuthHttp} http */
  constructor(http) { this.#http = http; }

  /**
   * @param {{project: string, name: string, scopes: readonly string[], allowSelfSubject?: boolean, signal?: AbortSignal}} options
   * @returns {Promise<TerraScaleResult<{userCode: string, verificationUri: string, verificationUriComplete: string, fingerprint: string, wait: () => Promise<TerraScaleResult<AgentIdentity>>}>>}
   */
  async enroll({ project, name, scopes, allowSelfSubject = false, signal }) {
    const path = `v1/${requireId(project, "project")}/agent-enrollments`;
    const pair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, false, ["sign", "verify"]);
    const { crv, kty, x, y } = await crypto.subtle.exportKey("jwk", pair.publicKey);
    const fingerprint = base64url(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(JSON.stringify({ crv, kty, x, y }))))).slice(0, 16);
    /** @type {TerraScaleResult<Enrollment>} */
    const started = await this.#http.request({ method: "POST", path, routeName: "tsauth.oidc.agent_enroll", signal,
      body: { name, requested_scopes: scopes, allow_self_subject: allowSelfSubject, public_jwk: { crv, kty, x, y } } });
    if (!started.ok) return started;
    const enrollment = started.value;
    const verificationUri = `${this.#http.config.apiBaseUrl}/ui/${project}/agent-enrollments`;
    if (!isValidId(enrollment.enrollment_id) || !Number.isInteger(enrollment.interval) || enrollment.interval < 1 || enrollment.interval > 600 ||
        !Number.isInteger(enrollment.expires_in) || enrollment.expires_in < 1 || enrollment.expires_in > 600 || !/^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$/u.test(enrollment.user_code) ||
        enrollment.verification_uri !== verificationUri || enrollment.verification_uri_complete !== `${verificationUri}?user_code=${enrollment.user_code}`)
      return err("tsauth.oidc.agent_enroll", { kind: "invalid-response", code: "invalid_enrollment", message: "Invalid agent enrollment response." });
    let interval = enrollment.interval * 1000;
    const deadline = Date.now() + enrollment.expires_in * 1000;
    const pollPath = `${path}/${enrollment.enrollment_id}/token`;
    /** @type {Promise<TerraScaleResult<AgentIdentity>> | undefined} */
    let pending;
    /** @returns {Promise<TerraScaleResult<AgentIdentity>>} */
    const poll = async () => {
      while (Date.now() < deadline) {
        await waitInterval(Math.min(interval, deadline - Date.now()), signal);
        if (Date.now() >= deadline) break;
        const proof = await assertion(pair.privateKey, `${this.#http.config.apiBaseUrl}/${pollPath}`);
        /** @type {TerraScaleResult<{agent_id: string, kid: string}>} */
        const result = await this.#http.request({ method: "POST", path: pollPath, routeName: "tsauth.oidc.agent_poll", body: { proof }, signal });
        if (result.ok) {
          if (!isValidId(result.value.agent_id) || !isValidId(result.value.kid))
            return err(result.routeName, { kind: "invalid-response", code: "invalid_agent", message: "Invalid enrollment identity." });
          return ok(result.routeName, result.status, result.headers, { agentId: result.value.agent_id, kid: result.value.kid, key: pair.privateKey });
        }
        if (result.error.code === "slow_down") interval += 5000;
        else if (result.error.code !== "authorization_pending") return result;
      }
      return err("tsauth.oidc.agent_poll", { kind: "authentication", code: "expired_token", message: "Agent enrollment expired." });
    };
    return ok(started.routeName, started.status, started.headers, { userCode: enrollment.user_code, verificationUri: enrollment.verification_uri,
      verificationUriComplete: enrollment.verification_uri_complete, fingerprint, wait: () => pending ??= poll() });
  }

  /** @param {AgentIdentity & {project: string, scope?: string, subject?: "owner" | "agent", signal?: AbortSignal}} options @returns {Promise<TerraScaleResult<AgentToken>>} */
  async getToken({ project, agentId, kid, key, scope, subject = "owner", signal }) {
    signal?.throwIfAborted();
    const path = `${requireId(project, "project")}/oauth/token`;
    requireId(agentId, "agentId");
    requireId(kid, "kid");
    const cacheKey = JSON.stringify([project, agentId, kid, scope, subject]);
    const cache = this.#tokens.get(key) ?? new Map();
    this.#tokens.set(key, cache);
    const cached = cache.get(cacheKey);
    if (cached && cached.expires > Date.now() + 30000)
      return ok("tsauth.oidc.agent_token", 200, new Headers(), { ...cached.token, expires_in: Math.floor((cached.expires - Date.now()) / 1000) });
    const signed = await assertion(key, `${this.#http.config.apiBaseUrl}/${path}`, { iss: agentId, sub: agentId, kid });
    const formBody = new URLSearchParams({ grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer", assertion: signed, requested_subject: subject });
    if (scope !== undefined) formBody.set("scope", scope);
    const sent = Date.now();
    /** @type {TerraScaleResult<AgentToken>} */
    const result = await this.#http.request({ method: "POST", path, routeName: "tsauth.oidc.agent_token", formBody, signal });
    if (result.ok) {
      const token = result.value;
      if (typeof token.access_token !== "string" || token.token_type !== "Bearer" || typeof token.scope !== "string" ||
          !Number.isInteger(token.expires_in) || token.expires_in < 1 || token.expires_in > 300)
        return err(result.routeName, { kind: "invalid-response", code: "invalid_token_response", message: "Invalid agent token response." });
      if (cache.size >= 32) cache.clear();
      cache.set(cacheKey, { token, expires: sent + token.expires_in * 1000 });
    }
    return result;
  }
}

/** @param {string} token @param {{project: string, audience?: string, apiBaseUrl?: string, fetch?: typeof fetch, signal?: AbortSignal}} options @returns {Promise<TerraScaleResult<VerifiedAgent>>} */
export async function verifyAgentToken(token, { project, audience, apiBaseUrl = "https://auth.terrascale.tech", fetch: fetchHandler, signal }) {
  requireId(project, "project");
  const http = new TsAuthHttp({ apiBaseUrl, ...(fetchHandler === undefined ? {} : { fetch: fetchHandler }) });
  const route = "tsauth.agents.verify";
  try {
    if (typeof token !== "string" || token.length > 16384) throw new TypeError();
    const parts = token.split(".");
    if (parts.length !== 3) throw new TypeError();
    const header = parseStrictJson(decode(parts[0] ?? ""));
    const claims = parseStrictJson(decode(parts[1] ?? ""));
    if (!header || typeof header !== "object" || !claims || typeof claims !== "object") throw new TypeError();
    const h = /** @type {Record<string, unknown>} */ (header);
    const c = /** @type {Record<string, unknown>} */ (claims);
    if (h.alg !== "ES256" || h.typ !== "at+jwt" || typeof h.kid !== "string" || ["crit", "jku", "jwk", "x5u", "x5c"].some(k => k in h)) throw new TypeError();
    const now = Date.now() / 1000;
    if (c.iss !== `${http.config.apiBaseUrl}/${project}` || c.token_use !== "agent" || c.actor_type !== "agent" ||
        !Number.isInteger(c.exp) || Number(c.exp) <= now || !Number.isInteger(c.iat) || Number(c.iat) > now + 30 ||
        Number(c.exp) <= Number(c.iat) || Number(c.exp) - Number(c.iat) > 300 ||
        (c.nbf !== undefined && (!Number.isInteger(c.nbf) || Number(c.nbf) > now)) ||
        (audience !== undefined && c.aud !== audience && !(Array.isArray(c.aud) && c.aud.includes(audience)))) throw new TypeError();
    /** @type {TerraScaleResult<{keys: (JsonWebKey & {kid: string})[]}>} */
    const jwks = await http.request({ method: "GET", path: `${project}/.well-known/jwks.json`, routeName: "tsauth.oidc.jwks", signal });
    if (!jwks.ok) return jwks;
    const matches = jwks.value.keys.filter(k => k.kid === h.kid && k.kty === "EC" && k.crv === "P-256" && k.alg === "ES256" && k.use === "sig" && k.d === undefined);
    if (matches.length !== 1) throw new TypeError();
    const key = await crypto.subtle.importKey("jwk", /** @type {JsonWebKey} */ (matches[0]), { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    if (!await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, decode(parts[2] ?? ""), new TextEncoder().encode(`${parts[0]}.${parts[1]}`))) throw new TypeError();
    const agentId = requireId(c.agent_id, "agent_id");
    const self = c.sub === agentId;
    const act = c.act;
    if (self ? act !== undefined : !act || typeof act !== "object" || Reflect.get(act, "sub") !== agentId || c.owner_sub !== undefined) throw new TypeError();
    const ownerId = requireId(self ? c.owner_sub : c.sub, "owner");
    if (ownerId === agentId) throw new TypeError();
    if (typeof c.scope !== "string" || c.scope.length === 0) throw new TypeError();
    return ok(route, 200, new Headers(), { isAgent: true, agentId, ownerId, actingFor: self ? "self" : "owner", scopes: c.scope.split(" ") });
  } catch {
    return err(route, { kind: "authentication", code: "invalid_agent_token", message: "Invalid agent access token." });
  }
}
