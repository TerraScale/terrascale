/* Signed subscription links. Confirm and manage links carry an HMAC of the
   subscriber, keyed by the SUBSCRIPTION_SECRET, so nothing secret is
   stored and a link cannot be forged or moved to another subscriber. Each
   generic webhook gets its own signing key derived the same way, so a
   receiver can verify payloads without learning the secret. */

export type Purpose = "confirm" | "manage" | "webhook";

async function key(secret: string): Promise<CryptoKey> {
  return crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, [
    "sign",
    "verify",
  ]);
}

export const base64url = (bytes: Uint8Array): string =>
  btoa(String.fromCharCode(...bytes))
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/, "");

export function decode(text: string): Uint8Array | null {
  try {
    const padded = text.replaceAll("-", "+").replaceAll("_", "/");
    return Uint8Array.from(atob(padded + "=".repeat((4 - (padded.length % 4)) % 4)), (char) => char.charCodeAt(0));
  } catch {
    return null;
  }
}

const message = (purpose: Purpose, id: string): Uint8Array => new TextEncoder().encode(`${purpose}:${id}`);

export async function sign(secret: string, purpose: Purpose, id: string): Promise<string> {
  const signature = await crypto.subtle.sign("HMAC", await key(secret), message(purpose, id));
  return base64url(new Uint8Array(signature));
}

/** The key a generic webhook's payloads are signed with. */
export const webhookKey = (secret: string, id: string): Promise<string> => sign(secret, "webhook", id);

/** Checks a token in constant time (HMAC verification). */
export async function verify(secret: string, purpose: Purpose, id: string, token: string): Promise<boolean> {
  const bytes = decode(token);
  if (!bytes || bytes.length !== 32) return false;
  return crypto.subtle.verify("HMAC", await key(secret), bytes, message(purpose, id));
}
