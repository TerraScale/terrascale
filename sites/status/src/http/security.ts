/* Headers every response carries. */

/** Edge and browser cache lifetime; well under the 30-second refresh. */
export const CACHE_SECONDS = 10;

export function secure(response: Response, cacheable: boolean): Response {
  const headers = new Headers(response.headers);
  headers.set("cache-control", cacheable ? `public, max-age=${CACHE_SECONDS}` : "no-store");
  headers.set("x-content-type-options", "nosniff");
  if (!headers.has("referrer-policy")) headers.set("referrer-policy", "strict-origin-when-cross-origin");
  headers.set("strict-transport-security", "max-age=31536000");
  headers.set("permissions-policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()");
  if (!headers.has("content-security-policy")) {
    headers.set(
      "content-security-policy",
      "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
    );
  }
  // The public /embed widget deliberately permits framing on other sites.
  if (headers.get("content-security-policy")?.includes("frame-ancestors 'none'")) {
    headers.set("x-frame-options", "DENY");
  }
  return new Response(response.body, { status: response.status, headers });
}
