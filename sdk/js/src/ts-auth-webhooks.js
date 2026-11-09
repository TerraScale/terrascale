import { createHmac, timingSafeEqual } from "node:crypto";

/**
 * Verification of a ts-auth end-user lifecycle webhook delivery failed.
 */
export class TsAuthWebhookError extends Error {
  /**
   * @param {string} code
   * @param {string} message
   */
  constructor(code, message) {
    super(message);
    this.name = "TsAuthWebhookError";
    this.code = code;
  }
}

const toleranceSeconds = 300;
const base64Pattern = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/;

/**
 * Verifies a Standard Webhooks delivery and returns the parsed event JSON.
 *
 * @param {string} secret the `whsec_` secret answered once at registration or rotation
 * @param {Record<string, string>} headers the delivery headers, looked up case-insensitively
 * @param {string | Uint8Array} rawBody the delivery body exactly as received
 * @param {() => number} [now] returns the current unix time in seconds
 * @returns {any}
 */
export function verifyWebhook(secret, headers, rawBody, now = () => Math.floor(Date.now() / 1000)) {
  if (typeof secret !== "string" || typeof headers !== "object" || headers === null) {
    throw new TypeError("verifyWebhook takes the whsec_ secret, a header record and the raw body.");
  }
  const lower = new Map(Object.entries(headers).map(([name, value]) => [name.toLowerCase(), value]));
  const id = lower.get("webhook-id");
  const timestamp = lower.get("webhook-timestamp");
  const signature = lower.get("webhook-signature");
  if (id === undefined || timestamp === undefined || signature === undefined) {
    throw new TsAuthWebhookError(
      "missing_headers",
      "The delivery must carry webhook-id, webhook-timestamp and webhook-signature.",
    );
  }
  if (!/^\d+$/.test(timestamp)) {
    throw new TsAuthWebhookError("invalid_timestamp", "webhook-timestamp must be unix seconds.");
  }
  if (Math.abs(now() - Number(timestamp)) > toleranceSeconds) {
    throw new TsAuthWebhookError(
      "timestamp_out_of_tolerance",
      `webhook-timestamp ${timestamp} is more than ${toleranceSeconds} seconds from now.`,
    );
  }
  const payload = secret.slice("whsec_".length);
  if (!secret.startsWith("whsec_") || payload === "" || !base64Pattern.test(payload)) {
    throw new TsAuthWebhookError(
      "invalid_secret",
      "The secret must be the whsec_ value answered once at registration or rotation.",
    );
  }
  const key = Buffer.from(payload, "base64");
  const body = typeof rawBody === "string" ? Buffer.from(rawBody, "utf8") : Buffer.from(rawBody);
  const mac = createHmac("sha256", key).update(`${id}.${timestamp}.`).update(body).digest();
  for (const entry of signature.split(" ")) {
    if (!entry.startsWith("v1,")) {
      continue;
    }
    const candidate = Buffer.from(entry.slice("v1,".length), "base64");
    if (candidate.length === mac.length && timingSafeEqual(mac, candidate)) {
      try {
        return JSON.parse(body.toString("utf8"));
      } catch (error) {
        throw new TsAuthWebhookError("invalid_body", "The delivery body is not valid JSON.");
      }
    }
  }
  throw new TsAuthWebhookError("signature_mismatch", "No v1 signature entry matched the delivery.");
}
