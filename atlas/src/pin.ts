import type { Env } from "./env";

export function b64urlEncode(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function b64urlDecode(text: string): Uint8Array<ArrayBuffer> {
  const binary = atob(text.replace(/-/g, "+").replace(/_/g, "/"));
  return Uint8Array.from(binary, (char) => char.charCodeAt(0));
}

function jsonB64url(value: unknown): string {
  return b64urlEncode(new TextEncoder().encode(JSON.stringify(value)));
}

interface SigningKey {
  privateKey: CryptoKey;
  publicKey: JsonWebKey;
  kid: string;
}
let cachedKey: { raw: string | undefined; value: Promise<SigningKey | null> } | undefined;

async function importSigningKey(raw: string | undefined): Promise<SigningKey | null> {
  if (!raw) return null;
  try {
    const jwk = JSON.parse(raw) as JsonWebKey;
    if (jwk.kty !== "EC" || jwk.crv !== "P-256" || !jwk.d || !jwk.x || !jwk.y) return null;
    const privateKey = await crypto.subtle.importKey("jwk", jwk, { name: "ECDSA", namedCurve: "P-256" }, false, [
      "sign",
    ]);
    const publicKey = { kty: "EC", crv: "P-256", x: jwk.x, y: jwk.y };
    const canonical = JSON.stringify({ crv: "P-256", kty: "EC", x: jwk.x, y: jwk.y });
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonical));
    return { privateKey, publicKey, kid: b64urlEncode(new Uint8Array(digest)) };
  } catch {
    return null;
  }
}

function signingKey(env: Env): Promise<SigningKey | null> {
  if (cachedKey === undefined || cachedKey.raw !== env.PIN_SIGNING_KEY) {
    cachedKey = { raw: env.PIN_SIGNING_KEY, value: importSigningKey(env.PIN_SIGNING_KEY) };
  }
  return cachedKey.value;
}

export async function publicJwks(env: Env): Promise<string | null> {
  const key = await signingKey(env);
  return key === null ? null : JSON.stringify({ keys: [{ ...key.publicKey, kid: key.kid, alg: "ES256", use: "sig" }] });
}

export interface PinClaims {
  project: string;
  database?: string;
  region: string;
  api: string;
}

export async function signPin(env: Env, claims: PinClaims, iat: number): Promise<{ pin: string; exp: number } | null> {
  const key = await signingKey(env);
  if (key === null) return null;
  const ttl = Number(env.PIN_TTL_S ?? "600");
  if (!Number.isSafeInteger(ttl) || ttl <= 0) return null;
  const exp = iat + ttl;
  const header = jsonB64url({ alg: "ES256", typ: "JWT", kid: key.kid });
  const body = jsonB64url({ iss: "atlas.terrascale.tech", ...claims, iat, exp });
  const data = new TextEncoder().encode(`${header}.${body}`);
  const raw = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key.privateKey, data);
  return { pin: `${header}.${body}.${b64urlEncode(new Uint8Array(raw))}`, exp };
}
