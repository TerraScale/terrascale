/* Private service-bound fetch handler: placement applies to fetch, not RPC.
   Trace the execution location instead of trusting the request's ingress colo. */

import { check } from "../probe/check";
import type { ProbeRequest, ProbeResponse, Target } from "../probe/protocol";

function isTarget(value: unknown): value is Target {
  if (typeof value !== "object" || value === null) return false;
  const target = value as Record<string, unknown>;
  return (
    typeof target.key === "string" &&
    typeof target.url === "string" &&
    typeof target.headers === "string" &&
    typeof target.body === "boolean"
  );
}

export async function colo(): Promise<string | null> {
  const trace = await check({
    key: "colo",
    url: "https://www.cloudflare.com/cdn-cgi/trace",
    headers: "{}",
    body: true,
  });
  return trace.status === 200 ? (/^colo=(\w+)$/m.exec(trace.body ?? "")?.[1] ?? null) : null;
}

export async function probe(request: Request, version: string | undefined, locate = colo): Promise<Response> {
  if (request.method !== "POST") return new Response("Method not allowed", { status: 405 });
  const body = (await request.json().catch(() => null)) as Partial<ProbeRequest> | null;
  if (!Array.isArray(body?.targets) || !body.targets.every(isTarget)) {
    return new Response("Bad request", { status: 400 });
  }
  const where = locate();
  const results = await Promise.all(body.targets.map((target) => check(target)));
  return Response.json({
    vantage: new URL(request.url).pathname.slice(1),
    version: version ?? "unknown",
    colo: await where,
    results,
  } satisfies ProbeResponse);
}
