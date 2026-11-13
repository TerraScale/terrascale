import { isObject, parsePublishBody, type PublishKind } from "./contract";
import type { Env } from "./env";
import { b64urlDecode, b64urlEncode } from "./pin";
import { invalidateMemory } from "./read";

const MAX_BODY_BYTES = 128 * 1024;
const keyCache = new Map<string, Promise<Map<string, CryptoKey> | null>>();

function failure(code: string, message: string, fix: string, status: number): Response {
  return Response.json({ error: { code, message, fix } }, { status });
}

async function importKeys(raw: string): Promise<Map<string, CryptoKey> | null> {
  try {
    const jwks: unknown = JSON.parse(raw);
    if (!isObject(jwks) || !Array.isArray(jwks.keys) || jwks.keys.length < 1 || jwks.keys.length > 4) return null;
    const keys = new Map<string, CryptoKey>();
    for (const jwk of jwks.keys) {
      if (
        !isObject(jwk) ||
        jwk.kty !== "EC" ||
        jwk.crv !== "P-256" ||
        typeof jwk.x !== "string" ||
        typeof jwk.y !== "string" ||
        "d" in jwk
      )
        return null;
      const key = await crypto.subtle.importKey(
        "jwk",
        { kty: "EC", crv: "P-256", x: jwk.x, y: jwk.y },
        { name: "ECDSA", namedCurve: "P-256" },
        false,
        ["verify"],
      );
      keys.set(`${jwk.x}.${jwk.y}`, key);
    }
    return keys;
  } catch {
    return null;
  }
}

function loadKeys(raw: string | undefined): Promise<Map<string, CryptoKey> | null> {
  if (!raw) return Promise.resolve(null);
  let keys = keyCache.get(raw);
  if (keys === undefined) {
    if (keyCache.size >= 2) keyCache.clear();
    keys = importKeys(raw);
    keyCache.set(raw, keys);
  }
  return keys;
}

async function authorizedKeys(env: Env, kind: PublishKind): Promise<CryptoKey[] | null> {
  if (kind !== "both") {
    const keys = await loadKeys(kind === "topology" ? env.TOPOLOGY_PUBLISH_KEYS : env.PLACEMENT_PUBLISH_KEYS);
    return keys === null ? null : [...keys.values()];
  }
  const [topology, placements] = await Promise.all([
    loadKeys(env.TOPOLOGY_PUBLISH_KEYS),
    loadKeys(env.PLACEMENT_PUBLISH_KEYS),
  ]);
  if (topology === null || placements === null) return null;
  return [...topology].filter(([id]) => placements.has(id)).map(([, key]) => key);
}

class BodyError extends Error {
  constructor(readonly code: "payload_too_large" | "body_timeout") {
    super(code);
  }
}

async function readBody(request: Request): Promise<Uint8Array<ArrayBuffer>> {
  const reader = request.body?.getReader();
  if (reader === undefined) return new Uint8Array();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new BodyError("body_timeout")), 10_000);
  });
  const consume = async () => {
    const chunks: Uint8Array[] = [];
    let total = 0;
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      total += next.value.length;
      if (total > MAX_BODY_BYTES) throw new BodyError("payload_too_large");
      chunks.push(next.value);
    }
    const result = new Uint8Array(total);
    let offset = 0;
    for (const chunk of chunks) {
      result.set(chunk, offset);
      offset += chunk.length;
    }
    return result;
  };
  try {
    return await Promise.race([consume(), timeout]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    await reader.cancel();
  }
}

export async function publish(request: Request, env: Env): Promise<Response> {
  const now = Math.floor(Date.now() / 1000);
  const timestamp = request.headers.get("X-Atlas-Timestamp");
  const signatureHeader = request.headers.get("X-Atlas-Signature");
  if (
    timestamp === null ||
    !/^[0-9]{1,12}$/.test(timestamp) ||
    Math.abs(now - Number(timestamp)) > 300 ||
    signatureHeader === null ||
    !/^[A-Za-z0-9_-]{86}$/.test(signatureHeader)
  ) {
    return failure(
      "invalid_signature",
      "Missing, expired or malformed publish headers",
      "Send a current X-Atlas-Timestamp and a base64url ES256 signature.",
      401,
    );
  }
  const signature = b64urlDecode(signatureHeader);
  if (signature.length !== 64 || b64urlEncode(signature) !== signatureHeader) {
    return failure(
      "invalid_signature",
      "Signature must be raw r||s",
      "Send a 64-byte ES256 signature encoded as base64url.",
      401,
    );
  }
  if (!env.TOPOLOGY_PUBLISH_KEYS && !env.PLACEMENT_PUBLISH_KEYS) {
    return failure(
      "publish_disabled",
      "No publish keys are configured",
      "Configure the matching public JWKS bindings.",
      503,
    );
  }
  let bytes: Uint8Array<ArrayBuffer>;
  try {
    bytes = await readBody(request);
  } catch (error) {
    if (!(error instanceof BodyError)) throw error;
    return failure(
      error.code,
      error.code === "body_timeout" ? "Body did not arrive within 10 seconds" : "Body exceeds 128 KiB",
      "Send a complete publish within the size limit.",
      error.code === "body_timeout" ? 408 : 413,
    );
  }
  let input: unknown;
  try {
    input = JSON.parse(new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes));
  } catch {
    return failure("empty_publish", "Body is not a JSON publish document", "Send topology or placements.", 400);
  }
  if (!isObject(input) || (!("topology" in input) && !("placements" in input))) {
    return failure("empty_publish", "No topology or placements supplied", "Send topology or placements.", 400);
  }
  const kind = "topology" in input ? ("placements" in input ? "both" : "topology") : "placements";
  const keys = await authorizedKeys(env, kind);
  if (keys === null) {
    return failure(
      "publish_disabled",
      "No keys authorize this publish kind",
      "Configure public P-256 JWKS; combined publishes need a shared key.",
      503,
    );
  }
  const prefix = new TextEncoder().encode(`atlas-publish-v1\n${timestamp}\n`);
  const signed = new Uint8Array(prefix.length + bytes.length);
  signed.set(prefix);
  signed.set(bytes, prefix.length);
  let verified = false;
  for (const key of keys) {
    if (await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, signature, signed)) {
      verified = true;
      break;
    }
  }
  if (!verified)
    return failure(
      "invalid_signature",
      "Signature verification failed",
      "Sign the exact body with a key authorized for every supplied section.",
      401,
    );
  const parsed = parsePublishBody(input, env, now);
  if (!parsed.ok) {
    return failure(
      parsed.code,
      parsed.code === "future_updated"
        ? "updated is more than 300 seconds ahead"
        : "Publish document failed validation",
      "Correct the document before publishing again.",
      400,
    );
  }
  const stub = env.ATLAS_STORE.get(env.ATLAS_STORE.idFromName("atlas"));
  const response = await stub.fetch("https://atlas-store/publish", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(parsed.request),
  });
  invalidateMemory();
  return response;
}
