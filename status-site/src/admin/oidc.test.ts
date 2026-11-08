import { afterEach, describe, expect, it, vi } from "vitest";
import app from "../app";
import type { Env } from "../env";
import { base64url } from "../notify/tokens";
import { testD1 } from "../testing/d1";
import { COOKIE, open, session } from "./auth";
import { CALLBACK, CLIENT, FLOW_COOKIE, ISSUER, ORGANIZATION, ORGANIZATION_URL } from "./oidc";

const USER = "01K00000000000000000000002";
const SECRET = "test-session-key-with-at-least-32-bytes";
const ORIGIN = new URL(CALLBACK).origin;
const encoder = new TextEncoder();
const ctx = { waitUntil() {}, passThroughOnException() {} } as unknown as ExecutionContext;
const encode = (value: object) => base64url(encoder.encode(JSON.stringify(value)));
afterEach(() => vi.restoreAllMocks());

async function setup(
  options: {
    claims?: Record<string, unknown>;
    role?: string | null;
    organization?: string;
    signature?: boolean;
    issuer?: string;
    user?: string;
    unavailable?: boolean;
    redirect?: boolean;
  } = {},
) {
  const keys = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, [
    "sign",
    "verify",
  ])) as CryptoKeyPair;
  const publicKey = {
    ...(await crypto.subtle.exportKey("jwk", keys.publicKey)),
    kid: "01K00000000000000000000003",
    alg: "ES256",
    use: "sig",
  };
  const env = {
    STATUS_DB: testD1(),
    ADMIN_SESSION_SECRET: SECRET,
    ADMIN_LOGIN_LIMITER: { limit: async () => ({ success: true }) },
  } as unknown as Env;
  const call = (path: string, init: RequestInit = {}) =>
    app.fetch(new Request(`${ORIGIN}${path}`, init) as Request<unknown, IncomingRequestCfProperties>, env, ctx);
  let authorization = new URL(ISSUER);
  let role = options.role === undefined ? "admin" : options.role;
  const accessToken = `${encode({ alg: "ES256", typ: "at+jwt", kid: publicKey.kid })}.${encode({ iss: ISSUER, sub: USER, aud: ISSUER, exp: Math.floor(Date.now() / 1000) + 3600 })}`;
  const accessSignature = base64url(
    new Uint8Array(
      await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, keys.privateKey, encoder.encode(accessToken)),
    ),
  );
  const access = `${accessToken}.${accessSignature}`;
  let redeemed = false;
  const network = vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
    // workerd rejects redirect:"error"; manual also prevents credentials following redirects.
    if (init?.redirect !== "manual") throw new TypeError("Unsupported Worker redirect mode");
    const url = String(input);
    if (options.redirect) return new Response(null, { status: 302, headers: { location: "https://other.test/" } });
    if (url === `${ISSUER}/.well-known/openid-configuration`)
      return Response.json({
        issuer: options.issuer ?? ISSUER,
        authorization_endpoint: `${ISSUER}/oauth/authorize`,
        token_endpoint: `${ISSUER}/oauth/token`,
        userinfo_endpoint: `${ISSUER}/oauth/userinfo`,
        jwks_uri: `${ISSUER}/.well-known/jwks.json`,
      });
    if (url === `${ISSUER}/oauth/token`) {
      if (redeemed) return Response.json({ error: "invalid_grant" }, { status: 400 });
      redeemed = true;
      const body = new URLSearchParams(String(init?.body));
      expect(body.get("client_id")).toBe(CLIENT);
      expect(body.get("redirect_uri")).toBe(CALLBACK);
      expect(body.has("client_secret")).toBe(false);
      expect(body.get("grant_type")).toBe("authorization_code");
      expect(
        base64url(new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(body.get("code_verifier")!)))),
      ).toBe(authorization.searchParams.get("code_challenge"));
      const token = `${encode({ alg: "ES256", typ: "JWT", kid: publicKey.kid })}.${encode({ iss: ISSUER, aud: CLIENT, sub: USER, nonce: authorization.searchParams.get("nonce"), iat: Math.floor(Date.now() / 1000), exp: Math.floor(Date.now() / 1000) + 3600, ...options.claims })}`;
      const signingKey =
        options.signature === false
          ? (
              (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, false, [
                "sign",
                "verify",
              ])) as CryptoKeyPair
            ).privateKey
          : keys.privateKey;
      const signature = base64url(
        new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, signingKey, encoder.encode(token))),
      );
      return Response.json({
        token_type: "Bearer",
        access_token: access,
        expires_in: 3600,
        id_token: `${token}.${signature}`,
      });
    }
    if (url === `${ISSUER}/.well-known/jwks.json`) return Response.json({ keys: [publicKey] });
    expect(init?.headers).toEqual({ authorization: `Bearer ${access}` });
    if (url === `${ISSUER}/oauth/userinfo`) return Response.json({ sub: options.user ?? USER });
    expect(url).toBe(ORGANIZATION_URL);
    return Response.json(
      { id: options.organization ?? ORGANIZATION, role },
      { status: options.unavailable ? 503 : 200 },
    );
  });
  const start = await call("/admin/authorize");
  if (start.status === 303) authorization = new URL(start.headers.get("location")!);
  const flowCookie = start.headers.get("set-cookie")?.split(";")[0] ?? "";
  const callback = (changes: Record<string, string> = {}, cookie = flowCookie) => {
    const query = new URLSearchParams({
      state: authorization.searchParams.get("state") ?? "",
      code: "single-use-code",
      iss: ISSUER,
      ...changes,
    });
    return call(`/admin/callback?${query}`, { headers: { cookie } });
  };
  return {
    env,
    access,
    start,
    authorization,
    callback,
    flowCookie,
    network,
    call,
    setRole: (value: string | null) => {
      role = value;
    },
  };
}

describe("admin OIDC authorization", () => {
  it("refuses identity-provider redirects without following them", async () => {
    const f = await setup({ redirect: true });
    expect(f.start.status).toBe(503);
    expect(f.network).toHaveBeenCalledTimes(1);
  });

  it.each(["admin", "owner"])("uses public PKCE and issues a protected session for TerraScale %s", async (role) => {
    const f = await setup({ role });
    expect(f.start.status).toBe(303);
    expect(f.authorization.origin).toBe(new URL(ISSUER).origin);
    expect(f.authorization.searchParams.get("code_challenge_method")).toBe("S256");
    expect(f.authorization.searchParams.get("scope")).toBe("openid");
    expect(f.start.headers.get("set-cookie")).toContain("HttpOnly; Secure; SameSite=Lax");
    expect(f.start.headers.get("set-cookie")).not.toContain("verifier");
    const response = await f.callback();
    expect(response.status).toBe(303);
    expect(response.headers.get("location")).toBe("/admin");
    expect(response.headers.get("referrer-policy")).toBe("no-referrer");
    const cookies = response.headers.get("set-cookie")!;
    expect(cookies).toContain(`${FLOW_COOKIE}=; Path=/; Max-Age=0`);
    expect(cookies).toContain("HttpOnly; Secure; SameSite=Lax");
    const value = cookies.match(new RegExp(`${COOKIE}=([^; ,]+)`))?.[1] ?? "";
    expect(value).toMatch(/^[A-Za-z0-9_-]{16}\.[A-Za-z0-9_-]+$/);
    expect(value).not.toContain(f.access);
    expect((await session(SECRET, value, Date.now()))?.accessToken).toBe(f.access);
    expect((await f.call("/admin", { headers: { cookie: `${COOKIE}=${value}` } })).status).toBe(200);
    f.setRole("member");
    expect((await f.call("/admin", { headers: { cookie: `${COOKIE}=${value}` } })).status).toBe(403);
  });

  it.each([{ role: "member" }, { role: "viewer" }, { role: null }, { organization: "01K00000000000000000000004" }])(
    "rejects non-admin or wrong-organization membership: %j",
    async (options) => {
      const response = await (await setup(options)).callback();
      expect(response.status).toBe(403);
      expect(response.headers.get("set-cookie") ?? "").not.toContain(`${COOKIE}=`);
    },
  );

  it.each([
    { claims: { nonce: "another-browser" } },
    { claims: { iss: "https://other.test/" } },
    { claims: { aud: "01K00000000000000000000004" } },
    { claims: { exp: 1 } },
    { claims: { iat: Math.floor(Date.now() / 1000) + 3600 } },
    { claims: { nbf: Math.floor(Date.now() / 1000) + 3600 } },
    { claims: { aud: [CLIENT, "another-client"] } },
    { claims: { at_hash: "unbound" } },
    { signature: false },
    { user: "01K00000000000000000000004" },
    { unavailable: true },
  ])("fails closed on invalid tokens or unavailable authorization: %j", async (options) => {
    const f = await setup(options);
    const response = await f.callback();
    expect(response.status).toBe(options.user ? 403 : 503);
    expect(response.headers.get("referrer-policy")).toBe("no-referrer");
    expect(response.headers.get("set-cookie") ?? "").not.toContain(`${COOKIE}=`);
  });

  it("rejects missing, mismatched, tampered and expired browser state before token exchange", async () => {
    const f = await setup();
    const flow = await open(SECRET, "oidc-flow", f.flowCookie.split("=")[1]!);
    expect(flow?.expires).toBeGreaterThan(Date.now());
    for (const [query, cookie] of [
      [{ state: "wrong" }, f.flowCookie],
      [{ iss: "https://other.test/" }, f.flowCookie],
      [{}, ""],
      [{}, `${f.flowCookie}x`],
    ] as const) {
      expect((await f.callback(query, cookie)).status).toBe(400);
    }
    vi.spyOn(Date, "now").mockReturnValue(Date.now() + 301_000);
    expect((await f.callback()).status).toBe(400);
    expect(f.network.mock.calls.some(([url]) => url === `${ISSUER}/oauth/token`)).toBe(false);
  });

  it("refuses replay of the provider's single-use authorization code", async () => {
    const f = await setup();
    expect((await f.callback()).status).toBe(303);
    expect((await f.callback()).status).toBe(503);
  });

  it("provides a navigation link and refuses the removed password POST", async () => {
    const f = await setup();
    expect(await (await f.call("/admin/login")).text()).toContain('href="/admin/authorize"');
    expect((await f.call("/admin/login", { method: "POST", headers: { origin: ORIGIN } })).status).toBe(405);
  });

  it("fails closed without the existing session secret", async () => {
    const f = await setup();
    delete f.env.ADMIN_SESSION_SECRET;
    f.network.mockClear();
    expect((await f.call("/admin/authorize")).status).toBe(503);
    expect(f.network).not.toHaveBeenCalled();
  });

  it("rejects discovery from a different issuer", async () => {
    expect((await setup({ issuer: "https://other.test/" })).start.status).toBe(503);
  });
});
