/* Public authorization-code client. Only the pinned issuer may receive credentials. */
import type { Env } from "../env";
import { isId } from "../ids";
import { base64url, decode } from "../notify/tokens";
import { issue, open, readCookie, seal, setCookie } from "./auth";

const AUTH_ORIGIN = "https://auth.terrascale.tech";
export const ISSUER = `${AUTH_ORIGIN}/terrascale`;
export const CLIENT = "00000000000000000000000006";
export const ORGANIZATION = "00000000000000000000000001";
export const ORGANIZATION_URL = `${AUTH_ORIGIN}/v1/terrascale/organizations/${ORGANIZATION}`;
export const CALLBACK = "https://status.terrascale.tech/admin/callback";
export const FLOW_COOKIE = "__Host-ts_status_oidc";
const encoder = new TextEncoder();
const random = () => base64url(crypto.getRandomValues(new Uint8Array(32)));
const flowCookie = (value: string, seconds: number) =>
  `${FLOW_COOKIE}=${value}; Path=/; Max-Age=${seconds}; HttpOnly; Secure; SameSite=Lax`;
const redirect = (location: string, cookies: string[]) => {
  const headers = new Headers({ location, "referrer-policy": "no-referrer" });
  for (const cookie of cookies) headers.append("set-cookie", cookie);
  return new Response(null, { status: 303, headers });
};

function sessionSecret(env: Env): string {
  if (!env.ADMIN_SESSION_SECRET) throw new Error("Admin session key is missing");
  return env.ADMIN_SESSION_SECRET;
}

async function json(url: string, init: RequestInit = {}): Promise<Record<string, unknown>> {
  const response = await fetch(url, { ...init, redirect: "manual", signal: AbortSignal.timeout(10_000) });
  if (!response.ok) throw new Error("Identity provider refused the request");
  return response.json();
}

async function discovery() {
  const metadata = await json(`${ISSUER}/.well-known/openid-configuration`);
  for (const [name, path] of Object.entries({
    issuer: "",
    authorization_endpoint: "oauth/authorize",
    token_endpoint: "oauth/token",
    userinfo_endpoint: "oauth/userinfo",
    jwks_uri: ".well-known/jwks.json",
  })) {
    if (metadata[name] !== (path ? `${ISSUER}/${path}` : ISSUER))
      throw new Error("Unexpected identity provider metadata");
  }
}

export async function begin(env: Env, now: number): Promise<Response> {
  const secret = sessionSecret(env);
  await discovery();
  const state = random(),
    nonce = random(),
    verifier = random();
  const challenge = base64url(new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(verifier))));
  const query = new URLSearchParams({
    client_id: CLIENT,
    redirect_uri: CALLBACK,
    response_type: "code",
    response_mode: "query",
    scope: "openid",
    state,
    nonce,
    code_challenge: challenge,
    code_challenge_method: "S256",
  });
  return redirect(`${ISSUER}/oauth/authorize?${query}`, [
    flowCookie(await seal(secret, "oidc-flow", { state, nonce, verifier, expires: now + 300_000 }), 300),
  ]);
}

function part(value: string): Record<string, unknown> {
  const bytes = decode(value);
  if (!bytes) throw new Error("Malformed identity token");
  return JSON.parse(new TextDecoder().decode(bytes));
}

async function identity(token: string, client: string, nonce: string, accessToken: string, now: number) {
  const [head, body, signature, ...rest] = token.split(".");
  if (!head || !body || !signature || rest.length) throw new Error("Malformed identity token");
  const header = part(head),
    claims = part(body),
    bytes = decode(signature);
  if (
    header.alg !== "ES256" ||
    header.typ !== "JWT" ||
    typeof header.kid !== "string" ||
    header.crit !== undefined ||
    !bytes
  )
    throw new Error("Unsupported identity token");
  const jwks = await json(`${ISSUER}/.well-known/jwks.json`);
  const keys = (Array.isArray(jwks.keys) ? jwks.keys : []).filter(
    (key) =>
      key.kid === header.kid &&
      key.kty === "EC" &&
      key.crv === "P-256" &&
      (key.alg === undefined || key.alg === "ES256") &&
      (key.use === undefined || key.use === "sig"),
  );
  if (keys.length !== 1) throw new Error("Unknown signing key");
  const key = await crypto.subtle.importKey("jwk", keys[0], { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  if (!(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, bytes, encoder.encode(`${head}.${body}`))))
    throw new Error("Invalid identity signature");
  const audiences = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
  if (
    claims.iss !== ISSUER ||
    !audiences.every((audience) => typeof audience === "string") ||
    !audiences.includes(client) ||
    (audiences.length > 1 && claims.azp !== client) ||
    (claims.azp !== undefined && claims.azp !== client) ||
    claims.nonce !== nonce ||
    typeof claims.sub !== "string" ||
    !isId(claims.sub) ||
    typeof claims.exp !== "number" ||
    !Number.isSafeInteger(claims.exp) ||
    claims.exp * 1000 <= now ||
    typeof claims.iat !== "number" ||
    !Number.isSafeInteger(claims.iat) ||
    claims.iat < 0 ||
    claims.iat > now / 1000 + 30 ||
    claims.iat > claims.exp ||
    (claims.nbf !== undefined &&
      (typeof claims.nbf !== "number" || !Number.isFinite(claims.nbf) || claims.nbf > now / 1000))
  )
    throw new Error("Invalid identity claims");
  if (
    claims.at_hash !== undefined &&
    claims.at_hash !==
      base64url(new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(accessToken))).slice(0, 16))
  )
    throw new Error("Access token binding failed");
  return { subject: claims.sub, expires: claims.exp * 1000 };
}

/** No cached roles: revocation and organization role changes apply to the next request. */
export async function authorized(accessToken: string): Promise<boolean> {
  const result = await json(ORGANIZATION_URL, {
    headers: { authorization: `Bearer ${accessToken}` },
  });
  return result.id === ORGANIZATION && (result.role === "owner" || result.role === "admin");
}

export async function callback(request: Request, env: Env, now: number): Promise<Response> {
  const secret = sessionSecret(env);
  const query = new URL(request.url).searchParams;
  const flow = await open(secret, "oidc-flow", readCookie(request, FLOW_COOKIE));
  if (
    new URL(request.url).origin !== new URL(CALLBACK).origin ||
    !flow ||
    typeof flow.expires !== "number" ||
    flow.expires <= now ||
    typeof flow.nonce !== "string" ||
    typeof flow.verifier !== "string" ||
    query.getAll("state").length !== 1 ||
    query.get("state") !== flow.state ||
    query.getAll("code").length !== 1 ||
    !query.get("code") ||
    query.has("error") ||
    query.getAll("iss").length !== 1 ||
    query.get("iss") !== ISSUER
  )
    return new Response("Sign-in expired or was refused. Start again.", {
      status: 400,
      headers: { "set-cookie": flowCookie("", 0), "referrer-policy": "no-referrer" },
    });
  await discovery();
  const tokens = await json(`${ISSUER}/oauth/token`, {
    method: "POST",
    body: new URLSearchParams({
      grant_type: "authorization_code",
      client_id: CLIENT,
      code: query.get("code")!,
      code_verifier: flow.verifier,
      redirect_uri: CALLBACK,
    }),
  });
  if (
    tokens.token_type !== "Bearer" ||
    typeof tokens.access_token !== "string" ||
    typeof tokens.id_token !== "string" ||
    typeof tokens.expires_in !== "number" ||
    !Number.isSafeInteger(tokens.expires_in) ||
    tokens.expires_in <= 0
  )
    throw new Error("Invalid token response");
  const verified = await identity(tokens.id_token, CLIENT, flow.nonce, tokens.access_token, now);
  const user = await json(`${ISSUER}/oauth/userinfo`, { headers: { authorization: `Bearer ${tokens.access_token}` } });
  if (user.sub !== verified.subject || !(await authorized(tokens.access_token)))
    return new Response("TerraScale owner or admin membership is required.", {
      status: 403,
      headers: { "set-cookie": flowCookie("", 0), "referrer-policy": "no-referrer" },
    });
  return redirect("/admin", [
    flowCookie("", 0),
    setCookie(
      await issue(secret, now, tokens.access_token, Math.min(verified.expires, now + tokens.expires_in * 1000)),
    ),
  ]);
}
