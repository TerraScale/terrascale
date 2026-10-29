import { requireId } from "../ids.js";
import { requireDatabaseSelector, requireName } from "./ids.js";
import { object, string } from "./decode.js";
import { list, nullable, numeric, record, text } from "./management-decode.js";
import { members, mutationOptions, pageQuery } from "./management-validation.js";
import { stringifyExactJson } from "./json.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
const webhook = record({ id: requireId, url: text, createdAt: text, status: text });
const webhooks = list(webhook);
const deliveries = list(record({ id: requireId, createdAt: text, status: text, attempts: numeric,
  lastAttemptAt: nullable(text), nextAttemptAt: nullable(text), httpStatus: nullable(numeric), errorCode: nullable(text) }));
/** @typedef {ReturnType<typeof webhook>} V1ChangeWebhook */
/** @typedef {V1ChangeWebhook & { readonly secret: string }} V1CreatedChangeWebhook */
/** @typedef {ReturnType<typeof deliveries>[number]} V1ChangeWebhookDelivery */
/** @typedef {{ readonly after?: string; readonly limit?: number }} V1ChangeWebhookListRequest */

/** @param {V1ChangeWebhookListRequest} request */
function query(request) {
  if (request.after !== undefined) requireId(request.after, "after");
  return pageQuery(request);
}

/** Collection change webhook registration and sanitized delivery history. Requests are never replayed. */
export class V1ChangeWebhooks {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string; collection: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database @param {string} collection */
  constructor(transport, database, collection) {
    this.#transport = transport;
    this.#params = { database: requireDatabaseSelector(database), collection: requireName("collection", collection) };
    Object.freeze(this);
  }
  /**
   * Registers from the current feed head. Requires documents:read and documents:write.
   * Store secret immediately; it is non-enumerable to keep it out of JSON and ordinary diagnostics.
   * @param {{ readonly url: string }} request @param {V1RequestOptions} [options] @returns {Promise<V1CreatedChangeWebhook>}
   */
  async create(request, options = {}) {
    members(request, ["url"]);
    if (typeof request.url !== "string" || request.url.length > 2048) throw new TypeError("Webhook URL must be an HTTPS URL of at most 2048 characters.");
    let url;
    try { url = new URL(request.url); } catch { throw new TypeError("Webhook URL must be absolute HTTPS."); }
    if (url.protocol !== "https:" || url.port !== "" || url.username !== "" || url.password !== "" || url.hash !== "")
      throw new TypeError("Webhook URL must be HTTPS on port 443, without credentials or a fragment.");
    mutationOptions(options);
    const raw = object((await this.#transport.call("createChangeWebhook", this.#params, {
      body: stringifyExactJson(request, "change webhook"), signal: options.signal,
    })).value, "created webhook");
    const secret = string(raw.secret, "webhook secret");
    return Object.freeze(Object.defineProperty({ ...webhook(raw), secret }, "secret", { enumerable: false }));
  }
  /**
   * Requires documents:read. Ascending IDs; continue with the final ID until an empty array. No secrets.
   * @param {V1ChangeWebhookListRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<readonly V1ChangeWebhook[]>}
   */
  async list(request = {}, options = {}) {
    return webhooks((await this.#transport.call("listChangeWebhooks", this.#params, { query: query(request), signal: options.signal })).value);
  }
  /**
   * Requires documents:read and documents:write. An in-flight delivery may still complete. Unknown ID returns 404.
   * @param {string} id @param {V1RequestOptions} [options] @returns {Promise<void>}
   */
  async delete(id, options = {}) {
    mutationOptions(options);
    await this.#transport.call("deleteChangeWebhook", { ...this.#params, webhook: requireId(id) }, { signal: options.signal });
  }
  /**
   * Requires documents:read. Latest 100 retained records; paginate with the final ID until an empty array.
   * @param {string} id @param {V1ChangeWebhookListRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<readonly V1ChangeWebhookDelivery[]>}
   */
  async deliveries(id, request = {}, options = {}) {
    return deliveries((await this.#transport.call("listChangeWebhookDeliveries", { ...this.#params, webhook: requireId(id) },
      { query: query(request), signal: options.signal })).value);
  }
}
