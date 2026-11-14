import { cacheKey, readQuery, type QuerySpec } from "./cache";
import { secure } from "./security";

export async function edgeCached(
  request: Request,
  ctx: ExecutionContext,
  origin: () => Promise<Response>,
  query?: QuerySpec,
): Promise<Response> {
  if (request.method !== "GET" && request.method !== "HEAD") return origin();
  const url = new URL(request.url);
  const fragment = url.searchParams.get("fragment") === "main";
  const key = new Request(cacheKey(url, readQuery(url, query), fragment));
  const cache = caches.default;
  const hit = await cache.match(key);
  const deliver = (response: Response): Response =>
    request.method === "HEAD" ? new Response(null, response) : response;
  if (hit) return deliver(secure(hit, true));
  const response = await origin();
  if (response.status === 200 && response.headers.get("cache-control")?.startsWith("public")) {
    ctx.waitUntil(cache.put(key, response.clone()));
  }
  return deliver(response);
}
