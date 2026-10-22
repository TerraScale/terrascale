import { requireId } from "../ids.js";
import { malformed } from "./decode.js";
import { list, numeric, record, text } from "./management-decode.js";
import { requireFilter } from "./filter.js";
import { stringifyExactJson } from "./json.js";
import { requireShapeHandle, requireShapeOffset } from "./shapes.js";
/** @import { V1Transport } from "./transport.js" */
const manifest = record({ handle: requireShapeHandle, objects: list(record({ offset: requireShapeOffset, next_offset: requireShapeOffset,
  binding: text, object: text, url: text, expires_at: text, epoch: text, key_id: requireId, sha256: text, size: numeric })) });
/** @typedef {ReturnType<typeof manifest>} V1ShapeManifest */
/** @typedef {{readonly handle: string; readonly offset: string; readonly where?: import("./filter.js").V1Filter}} V1ShapeManifestRequest */
/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @param {V1ShapeManifestRequest} request @param {AbortSignal | undefined} signal @returns {Promise<V1ShapeManifest>} */
export async function readShapeManifest(transport, params, request, signal) {
  const handle = requireShapeHandle(request.handle);
  const offset = requireShapeOffset(request.offset);
  if (offset === "-1") throw new TypeError("A manifest requires a snapshot or log offset.");
  const query = new URLSearchParams({ handle, offset });
  if (request.where !== undefined) query.set("where", stringifyExactJson(requireFilter(request.where), "shape filter"));
  const response = await transport.call("getShapeManifest", params, { query, cache: "no-store", signal });
  const result = manifest(response.value);
  if (result.handle !== handle || result.objects.length > 8) malformed("shape manifest binding");
  let next = offset;
  for (const item of result.objects) {
    if (item.offset !== next || item.next_offset === item.offset || item.offset === "-1" || item.next_offset === "-1" ||
        !/^[a-f0-9]{64}$/u.test(item.sha256) || !/^(?:0|[1-9][0-9]*)$/u.test(item.epoch)) malformed("shape manifest object");
    const url = new URL(item.url);
    if (url.protocol !== "https:" || url.username !== "" || url.password !== "" || url.hash !== "") malformed("shape object URL");
    next = item.next_offset;
  }
  return result;
}
