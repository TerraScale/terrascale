import { TsAuthOidcClient } from "./ts-auth.js";
import { TsAuthHttp } from "./http.js";
import { requireId } from "./ids.js";
import { err, ok } from "./result.js";
import { TerraBaseV1Error } from "./v1/errors.js";

/** @typedef {import("./ts-auth.js").TsAuthOidcUserInfo} BrowserUser */
/** @typedef {import("./ts-auth.js").TsAuthOidcTokenResponse} Tokens */
/** @typedef {{ getItem(key: string): string | null | Promise<string | null>; setItem(key: string, value: string): void | Promise<void>; removeItem(key: string): void | Promise<void> }} BrowserAuthStorage */
/** @typedef {{ project: string; clientId: string; authBaseUrl: string; redirectUri: string; storage?: BrowserAuthStorage; pkceStorage?: BrowserAuthStorage }} BrowserAuthConfig */

/** A public tenant client. Credentials default to memory; secure storage is replaceable. */
export class BrowserAuth {
  /** @type {import("./ts-auth.js").TsAuthOidcClient} */
  #oidc;
  #clientId;
  #redirect;
  #storageKey;
  #issuer;
  #storage;
  #pkce;
  #storageWrites = Promise.resolve();
  /** @type {{ tokens: Tokens; expires: number; user: BrowserUser } | undefined} */
  #session;
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  #timer;
  /** @type {Promise<import("./result.js").TerraScaleResult<BrowserUser>> | undefined} */
  #refresh;
  #generation = 0;

  /** @param {BrowserAuthConfig} config */
  constructor(config) {
    const project = requireId(config.project, "project");
    this.#clientId = requireId(config.clientId, "clientId");
    const redirect = new URL(config.redirectUri);
    if ((redirect.protocol !== "https:" && !(redirect.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(redirect.hostname))) ||
        redirect.username || redirect.password || redirect.hash)
      throw new TypeError("redirectUri must be an HTTPS callback, or HTTP loopback, without credentials or a fragment.");
    this.#redirect = redirect.href;
    const http = new TsAuthHttp({ apiBaseUrl: config.authBaseUrl, maxRetryAttempts: 0 });
    this.#issuer = http.config.apiBaseUrl + "/" + project;
    this.#oidc = new TsAuthOidcClient(http, project);
    this.#storageKey = `terrascale.pkce:${this.#issuer}:${this.#clientId}`;
    this.#storage = config.storage;
    this.#pkce = config.pkceStorage ?? {
      getItem: key => sessionStorage.getItem(key), setItem: (key, value) => sessionStorage.setItem(key, value),
      removeItem: key => sessionStorage.removeItem(key),
    };
  }

  /** @returns {BrowserUser | null} */
  get currentUser() { return this.#session?.user ?? null; }

  /** Starts hosted sign-in, including the tenant's configured magic-link method. */
  async signIn() {
    const verifier = random();
    const state = random();
    const challenge = base64(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier))));
    await this.#pkce.setItem(this.#storageKey, JSON.stringify({ verifier, state, created: Date.now() }));
    location.assign(this.#oidc.authorizationUrl({
      client_id: this.#clientId, redirect_uri: this.#redirect, response_type: "code",
      scope: "openid email profile offline_access", state, code_challenge: challenge, code_challenge_method: "S256",
    }).href);
  }

  /** Completes a one-use PKCE callback and removes OAuth query parameters from browser history. */
  async completeSignIn() {
    const callback = new URL(location.href);
    const expected = new URL(this.#redirect);
    if (callback.origin !== expected.origin || callback.pathname !== expected.pathname)
      throw new TypeError("The current page is not the registered callback.");
    const raw = await this.#pkce.getItem(this.#storageKey);
    await this.#pkce.removeItem(this.#storageKey);
    const code = callback.searchParams.get("code");
    const state = callback.searchParams.get("state");
    const failure = callback.searchParams.get("error");
    for (const name of ["code", "state", "error", "error_description", "iss"])
      callback.searchParams.delete(name);
    history.replaceState(history.state, "", callback.href);
    const pending = raw === null ? null : JSON.parse(raw);
    if (!pending || typeof pending.verifier !== "string" || typeof pending.state !== "string" ||
        pending.state !== state || typeof pending.created !== "number" ||
        Date.now() - pending.created > 600_000 || Date.now() < pending.created)
      return authFailure("invalid_state", "The sign-in state is missing or expired.");
    if (failure || !code) return authFailure("sign_in_failed", "Hosted sign-in did not return an authorization code.");
    const generation = ++this.#generation;
    return this.#accept(await this.#oidc.exchangePublic(this.#clientId, code, this.#redirect, pending.verifier), generation);
  }

  /** Signs in a new anonymous user, subject to the tenant's guest admission policy. */
  async signInGuest() {
    const generation = ++this.#generation;
    const failure = await this.#forget();
    if (failure) return failure;
    return this.#accept(await this.#oidc.signInGuest(this.#clientId), generation);
  }

  /** Restores credentials only from an explicitly supplied secure storage adapter, checking userinfo again. */
  async restore() {
    const generation = ++this.#generation;
    try {
      const raw = await this.#storage?.getItem(this.#storageKey + ":tokens");
      return raw ? this.#accept(ok("tsauth.oidc.restore", 200, new Headers(), JSON.parse(raw)), generation)
        : authFailure("signed_out", "No stored sign-in exists.");
    } catch { return authFailure("storage_unavailable", "The credential store could not be read."); }
  }

  /** Rotates the refresh token once; concurrent callers share the same exchange. */
  refresh() {
    if (this.#refresh !== undefined) return this.#refresh;
    const session = this.#session;
    if (!session?.tokens.refresh_token)
      return Promise.resolve(authFailure("signed_out", "Sign in before requesting a token."));
    const generation = this.#generation;
    this.#refresh = this.#oidc.refreshPublic(this.#clientId, session.tokens.refresh_token, { scope: session.tokens.scope })
      .then(result => this.#accept(result, generation))
      .finally(() => { this.#refresh = undefined; });
    return this.#refresh;
  }

  /** Supplies a current access token to the data client, refreshing before exp. @returns {Promise<string>} */
  async accessToken() {
    const rotating = this.#refresh ?? (this.#session && this.#session.expires - Date.now() <= 30_000 ? this.refresh() : undefined);
    if (rotating) {
      const refreshed = await rotating;
      if (!refreshed.ok) throw new TerraBaseV1Error({ status: refreshed.status ?? 401, code: refreshed.error.code, message: refreshed.error.message });
    }
    if (!this.#session) throw new TerraBaseV1Error({ status: 401, code: "signed_out" });
    return this.#session.tokens.access_token;
  }

  /** Clears local credentials and revokes this sign-in's refresh family. Hosted cookies are ended through logoutUrl. */
  async signOut() {
    const token = this.#session?.tokens.refresh_token;
    ++this.#generation;
    let failure = await this.#forget();
    try { await this.#pkce.removeItem(this.#storageKey); }
    catch { failure = authFailure("storage_unavailable", "The sign-in store could not be cleared."); }
    const revoked = token ? await this.#oidc.revokePublic(this.#clientId, token) : ok("tsauth.oidc.revoke", 200, new Headers(), undefined);
    return revoked.ok && failure ? failure : revoked;
  }

  /** Ends the hosted session through ts-auth's confirmation page. @returns {URL} */
  get logoutUrl() {
    return new URL(this.#issuer + "/oauth/logout?client_id=" + encodeURIComponent(this.#clientId));
  }

  /** @param {import("./result.js").TerraScaleResult<Tokens>} result @param {number} generation @returns {Promise<import("./result.js").TerraScaleResult<BrowserUser>>} */
  async #accept(result, generation) {
    if (generation !== this.#generation) return this.#changed(result);
    if (!result.ok) {
      return await this.#forget() ?? result;
    }
    try {
      const claims = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(result.value.access_token.split(".")[1]?.replaceAll("-", "+").replaceAll("_", "/") ?? ""), c => c.charCodeAt(0))));
      if (claims.iss !== this.#issuer || claims.token_use !== "user" || !Number.isSafeInteger(claims.exp) ||
          claims.exp * 1000 <= Date.now() + 30_000 || typeof claims.sub !== "string" ||
          !result.value.refresh_token || typeof result.value.scope !== "string")
        throw new TypeError("Invalid tenant token.");
      const user = await this.#oidc.userinfo(result.value.access_token);
      if (generation !== this.#generation) return this.#changed(result);
      if (!user.ok) return await this.#forget() ?? user;
      if (user.value.sub !== claims.sub) throw new TypeError("Invalid tenant user.");
      await this.#persist(result.value);
      if (generation !== this.#generation) return this.#changed(result);
      clearTimeout(this.#timer);
      this.#session = { tokens: result.value, user: Object.freeze(user.value), expires: claims.exp * 1000 };
      this.#timer = setTimeout(() => { void this.refresh(); }, Math.min(2_147_483_647, this.#session.expires - Date.now() - 30_000));
      return { ...result, value: this.#session.user };
    } catch {
      return await this.#forget() ?? authFailure("invalid_response", "ts-auth returned an invalid tenant sign-in or credentials could not be stored.");
    }
  }

  async #forget() {
    this.#clear();
    try { await this.#persist(); }
    catch { return authFailure("storage_unavailable", "The credential store could not be cleared."); }
    return undefined;
  }

  /** @param {Tokens} [tokens] */
  #persist(tokens) {
    const write = async () => tokens ? this.#storage?.setItem(this.#storageKey + ":tokens", JSON.stringify(tokens))
      : this.#storage?.removeItem(this.#storageKey + ":tokens");
    this.#storageWrites = this.#storageWrites.then(write, write);
    return this.#storageWrites;
  }

  /** @param {import("./result.js").TerraScaleResult<Tokens>} result */
  async #changed(result) {
    if (result.ok && result.value.refresh_token) {
      const revoked = await this.#oidc.revokePublic(this.#clientId, result.value.refresh_token);
      if (!revoked.ok) return revoked;
    }
    return authFailure("session_changed", "The sign-in changed during this request.");
  }

  #clear() {
    clearTimeout(this.#timer);
    this.#timer = undefined;
    this.#session = undefined;
  }
}

/** @param {string} code @param {string} message */
function authFailure(code, message) {
  return err("tsauth.oidc.token", { kind: code === "invalid_response" ? "invalid-response" : "authentication", code, message });
}

/** @param {Uint8Array} bytes */
function base64(bytes) {
  return btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

function random() { return base64(crypto.getRandomValues(new Uint8Array(32))); }
