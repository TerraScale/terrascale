/** @typedef {"permissive" | "required"} LocalAuthenticationMode */
/**
 * @param {unknown} value
 * @param {number} [status]
 * @returns {Response}
 */
export function jsonResponse(value, status = 200) {
  const body = JSON.stringify(value);
  if (new TextEncoder().encode(body).byteLength > 4 * 1024 * 1024)
    return problemResponse(503, "capacity");
  return new Response(body, {
    status,
    headers: { "content-type": "application/json" },
  });
}
/**
 * @param {number} status
 * @param {string} code
 * @returns {Response}
 */
export function problemResponse(status, code) {
  return jsonResponse({ error: { code } }, status);
}
