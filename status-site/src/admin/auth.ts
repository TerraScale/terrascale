/* Encrypted admin sessions and session-bound CSRF tokens. */

import { base64url, decode } from "../notify/tokens";

export const COOKIE = "__Host-ts_status_admin";

/** A session lasts this long; signing in again starts a new one. */
export const SESSION_MS = 60 * 60_000;

const encoder = new TextEncoder();

/** Compares two byte strings without exiting early on the first difference. */
export function sameBytes(a: Uint8Array, b: Uint8Array): boolean {
  let difference = a.length ^ b.length;
  for (let index = 0; index < Math.max(a.length, b.length); index++) difference |= (a[index] ?? 0) ^ (b[index] ?? 0);
  return difference === 0;
}

async function hmac(secret: string, text: string): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey("raw", encoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, [
    "sign",
  ]);
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, encoder.encode(text)));
}

/** Encrypted cookies keep the sign-in access token and PKCE verifier out of browser-readable data. */
async function cookieKey(secret: string): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    "raw",
    await crypto.subtle.digest("SHA-256", encoder.encode(secret)),
    "AES-GCM",
    false,
    ["encrypt", "decrypt"],
  );
}

export async function seal(secret: string, purpose: string, value: object): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const encrypted = await crypto.subtle.encrypt(
    { name: "AES-GCM", iv, additionalData: encoder.encode(purpose) },
    await cookieKey(secret),
    encoder.encode(JSON.stringify(value)),
  );
  return `${base64url(iv)}.${base64url(new Uint8Array(encrypted))}`;
}

export async function open(
  secret: string,
  purpose: string,
  value: string | null,
): Promise<Record<string, unknown> | null> {
  try {
    const [iv, body, ...rest] = (value ?? "").split(".");
    const nonce = decode(iv ?? "");
    const bytes = decode(body ?? "");
    if (!nonce || nonce.length !== 12 || !bytes || rest.length) return null;
    return JSON.parse(
      new TextDecoder().decode(
        await crypto.subtle.decrypt(
          { name: "AES-GCM", iv: nonce, additionalData: encoder.encode(purpose) },
          await cookieKey(secret),
          bytes,
        ),
      ),
    );
  } catch {
    return null;
  }
}

export interface Session {
  expires: number;
  nonce: string;
  accessToken: string;
}

export async function issue(secret: string, now: number, accessToken: string, expires: number): Promise<string> {
  const value = await seal(secret, "admin-session", {
    expires: Math.min(now + SESSION_MS, expires),
    nonce: base64url(crypto.getRandomValues(new Uint8Array(16))),
    accessToken,
  });
  if (value.length > 3800) throw new Error("Admin session exceeds cookie capacity");
  return value;
}

export async function session(secret: string | undefined, value: string | null, now: number): Promise<Session | null> {
  const current = secret ? await open(secret, "admin-session", value) : null;
  return current &&
    typeof current.expires === "number" &&
    current.expires > now &&
    typeof current.nonce === "string" &&
    typeof current.accessToken === "string"
    ? (current as unknown as Session)
    : null;
}

/** The token every admin form posts back, bound to the session. */
export const csrfToken = async (secret: string, current: Session): Promise<string> =>
  base64url(await hmac(secret, `csrf.${current.nonce}`));

export async function checkCsrf(secret: string, current: Session, presented: string | null): Promise<boolean> {
  const bytes = decode(presented ?? "");
  return bytes !== null && sameBytes(bytes, await hmac(secret, `csrf.${current.nonce}`));
}

/** The admin cookie's value from a request. */
export function readCookie(request: Request, cookie = COOKIE): string | null {
  for (const part of (request.headers.get("cookie") ?? "").split(";")) {
    const [name, ...value] = part.trim().split("=");
    if (name === cookie) return value.join("=");
  }
  return null;
}

export const setCookie = (value: string): string =>
  `${COOKIE}=${value}; Path=/; Max-Age=${SESSION_MS / 1000}; HttpOnly; Secure; SameSite=Lax`;

export const clearCookie = `${COOKIE}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax`;

/** Whether a form post comes from this site: browsers send Origin on every POST. */
export function sameOrigin(request: Request): boolean {
  return request.headers.get("origin") === new URL(request.url).origin;
}
