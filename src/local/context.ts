export type LocalAuthenticationMode = "permissive" | "required";
export function jsonResponse(value: unknown, status = 200): Response {
  const body = JSON.stringify(value);
  if (new TextEncoder().encode(body).byteLength > 4 * 1024 * 1024)
    return problemResponse(503, "capacity");
  return new Response(body, {
    status,
    headers: { "content-type": "application/json" },
  });
}
export function problemResponse(status: number, code: string): Response {
  return jsonResponse({ error: { code } }, status);
}
