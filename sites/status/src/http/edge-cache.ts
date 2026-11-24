import { cacheKey } from "./cache";
import { secure } from "./security";

export async function edgeCached(
  request: Request,
  ctx: ExecutionContext,
  origin: () => Promise<Response>,
  query: Readonly<Record<string, string>>,
  fragment: boolean,
  direct = false,
): Promise<Response> {
  const deliver = (response: Response): Response =>
    request.method === "HEAD" ? new Response(null, response) : response;
  if (direct) return deliver(secure(await origin(), false));
  const key = new Request(cacheKey(new URL(request.url), query, fragment));
  const cache = caches.default;
  const hit = await cache.match(key);
  if (hit) return deliver(secure(hit, true));
  const response = await origin();
  if (response.status === 200 && response.headers.get("cache-control")?.startsWith("public")) {
    ctx.waitUntil(cache.put(key, response.clone()));
  }
  return deliver(response);
}
