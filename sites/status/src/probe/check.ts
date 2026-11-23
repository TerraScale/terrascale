/* One HTTP observation of a target: its status, time to headers, and on
   request the start of its body. Judging it is up to the coordinator. */

import type { Target, TargetResult } from "./protocol";

/** A probe slower than this fails. */
export const PROBE_TIMEOUT_MS = 5_000;

/** Bodies are read up to this size; checks look at their start only. */
export const MAX_BODY_BYTES = 64 * 1024;

export const USER_AGENT = "TerraScale-Status/2 (+https://status.terrascale.tech)";

async function readStart(body: ReadableStream<Uint8Array>): Promise<string> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let text = "";
  let size = 0;
  while (size < MAX_BODY_BYTES) {
    const { done, value } = await reader.read();
    if (done) return (text + decoder.decode()).slice(0, MAX_BODY_BYTES);
    size += value.byteLength;
    text += decoder.decode(value, { stream: true });
  }
  await reader.cancel();
  return text.slice(0, MAX_BODY_BYTES);
}

export async function check(target: Target, fetcher: typeof fetch = fetch): Promise<TargetResult> {
  const started = Date.now();
  // A cleared timer, unlike AbortSignal.timeout, lets the Worker finish as
  // soon as the probe does.
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(new DOMException("Timed out", "TimeoutError")), PROBE_TIMEOUT_MS);
  try {
    const response = await fetcher(target.url, {
      headers: { "user-agent": USER_AGENT, ...(JSON.parse(target.headers) as Record<string, string>) },
      redirect: "manual",
      cache: "no-store",
      signal: controller.signal,
    });
    const latencyMs = Date.now() - started;
    const result: TargetResult = { key: target.key, status: response.status, latencyMs, error: null };
    if (target.body && response.body) result.body = await readStart(response.body);
    else await response.body?.cancel();
    return result;
  } catch (error) {
    const timedOut = error instanceof DOMException && error.name === "TimeoutError";
    return {
      key: target.key,
      status: null,
      latencyMs: Date.now() - started,
      error: timedOut ? "Timed out" : "Unreachable",
    };
  } finally {
    clearTimeout(timer);
  }
}
