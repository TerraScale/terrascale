import {
  createHash,
  createHmac,
  randomBytes,
  randomUUID,
  timingSafeEqual,
} from "node:crypto";
import {
  decodeDatabaseAuthority,
  decodeDatabasePoint,
  decodeDatabaseStatus,
  decodeDatabaseValue,
  encodeDatabaseAuthority,
  encodeDatabaseCommit,
  encodeDatabaseQuery,
  encodeDatabaseStatus,
  encodeDatabaseSubscription,
  encodeDatabaseValue,
  parseDatabaseJson,
  requireDatabaseU64,
  requireDatabaseUuid,
} from "../database-codec.js";
import { matchesLocalBearerAuthorization } from "./authentication.js";
import { jsonResponse, problemResponse } from "./context.js";
/**
 * @import {
 *   CollectionBinding,
 *   DatabaseCommit,
 *   DatabaseCondition,
 *   DatabaseField,
 *   DatabasePoint,
 *   DatabaseQueryPredicate,
 *   DatabaseScope,
 *   DatabaseValue,
 *   DatabaseWrite,
 * } from "../database-types.js"
 */
/** @import { FetchLike } from "../config.js" */
/** @import { LocalAuthenticationMode } from "./context.js" */
/** @import { TerraScaleSqliteStore } from "./store.js" */

/** @typedef {Record<string, unknown>} JsonObject */
/** @typedef {JsonObject} WirePoint */
/**
 * @typedef {{
 *   readonly sequence: string;
 *   readonly entries: readonly {
 *     readonly collection: string;
 *     readonly document: string;
 *     readonly before: WirePoint;
 *     readonly after: WirePoint;
 *   }[];
 * }} Change
 */
/**
 * @typedef {{
 *   readonly route: string;
 *   readonly predicate: string;
 *   readonly limit: number;
 *   readonly sequence: string;
 *   readonly rows: readonly {
 *     readonly document: string;
 *     readonly point: WirePoint;
 *   }[];
 *   readonly expires: number;
 * }} Snapshot
 */
const absent = {
  version: 1,
  state: "absent",
  state_version: null,
  context: [],
  siblings: [],
};
const maximumBodyBytes = 4 * 1024 * 1024;

/** Development implementation of the admitted native grammar, never producer qualification. */
export class TerraScaleLocalRouter {
  /** @type {TerraScaleSqliteStore} */
  #store;
  /** @type {DatabaseScope} */
  #scope;
  /** @type {string} */
  #scopeKey;
  /** @type {string} */
  #path;
  /** @type {string | undefined} */
  #token;
  #secret = randomBytes(32);
  /** @type {{ readonly replica: string; readonly incarnation: string }} */
  #writer;

  /**
   * @param {TerraScaleSqliteStore} store
   * @param {DatabaseScope} scope
   * @param {{
   *   readonly authentication: LocalAuthenticationMode;
   *   readonly bearerToken?: string;
   * }} options
   */
  constructor(store, scope, options) {
    this.#store = store;
    this.#scope = Object.freeze({
      tenant: requireDatabaseUuid(scope.tenant),
      database: requireDatabaseUuid(scope.database),
      shard: requireDatabaseU64(scope.shard),
    });
    this.#scopeKey = `${scope.tenant}/${scope.database}/${scope.shard}`;
    this.#path = `/v1/tenants/${scope.tenant}/databases/${scope.database}/shards/${scope.shard}`;
    this.#token =
      options.authentication === "required" ? options.bearerToken : undefined;
    const writer = /** @type {{ readonly replica: string; readonly incarnation: string } | undefined} */ (
      store.getMeta(`native-writer:${this.#scopeKey}`)
    ) ?? {
      replica: randomUUID(),
      incarnation: randomBytes(16).toString("hex"),
    };
    this.#writer = Object.freeze(writer);
    store.setMeta(`native-writer:${this.#scopeKey}`, writer);
  }

  get authority() {
    return Object.freeze({
      mode: /** @type {const} */ ("single_writer"),
      generation: 1n,
      writer: this.#writer,
    });
  }
  /**
   * @param {CollectionBinding} binding
   * @returns {void}
   */
  bindCollection(binding) {
    requireDatabaseUuid(binding.collection);
    requireDatabaseU64(binding.schema_generation, "schema generation", true);
    if (!/^[0-9a-f]{64}$/u.test(binding.schema_digest))
      throw new TypeError("Schema digest must be lowercase SHA256.");
    this.#store.put("native-catalog", this.#scopeKey, binding.collection, {
      ...binding,
      schema_generation: binding.schema_generation.toString(),
    });
  }
  /**
   * @readonly
   * @type {FetchLike}
   */
  fetch = async (input, init) => this.handle(new Request(input, init));

  /**
   * @param {Request} request
   * @returns {Promise<Response>}
   */
  async handle(request) {
    if (
      this.#token !== undefined &&
      !matchesLocalBearerAuthorization(
        request.headers.get("authorization"),
        this.#token,
      )
    )
      return problemResponse(401, "invalid_key");
    try {
      request.signal.throwIfAborted();
      const url = new URL(request.url);
      if (url.search !== "" || !url.pathname.startsWith(`${this.#path}/`))
        return problemResponse(403, "forbidden_scope");
      const suffix = url.pathname.slice(this.#path.length);
      if (request.method === "GET") {
        const match = /^\/collections\/([^/]+)\/documents\/([^/]+)$/u.exec(
          suffix,
        );
        if (match === null) return problemResponse(404, "unknown_route");
        const collection = requireDatabaseUuid(match[1]);
        const document = requireDatabaseUuid(match[2]);
        return await this.#store.exclusive(() => {
          this.#binding(collection);
          return jsonResponse(this.#point(collection, document));
        });
      }
      if (request.method !== "POST")
        return problemResponse(405, "method_not_allowed");
      if (
        request.headers.get("content-type")?.split(";", 1)[0]?.trim() !==
        "application/json"
      )
        return problemResponse(400, "invalid_request");
      const raw = object(await readBody(request));
      if (suffix === "/transactions") {
        const commit = decodeCommit(raw);
        const encoded = await encodeDatabaseCommit(this.#scope, commit);
        if (encoded.effect_digest !== raw.effect_digest)
          return problemResponse(400, "invalid_request");
        return await this.#store.exclusive(() =>
          this.#store.transaction(() => this.#commit(raw, commit)),
        );
      }
      if (suffix === "/transactions/status") {
        const identity = decodeDatabaseStatus({
          ...raw,
          state: "unknown",
          outcome: null,
        });
        exactKeys(raw, [
          "version",
          "transaction_id",
          "authority",
          "effect_digest",
        ]);
        encodeDatabaseStatus({
          transaction_id: identity.transaction_id,
          authority: identity.authority,
          effect_digest: identity.effect_digest,
        });
        return await this.#store.exclusive(() => {
          const result = /** @type {JsonObject | undefined} */ (
            this.#store.get(
              "native-transactions",
              this.#scopeKey,
              transactionKey(raw),
            )
          );
          if (result !== undefined) {
            if (
              result.effect_digest !== raw.effect_digest ||
              JSON.stringify(result.authority) !== JSON.stringify(raw.authority)
            )
              return problemResponse(409, "idempotency_conflict");
            if (
              identity.transaction_id.expires_at_unix_ms <= BigInt(Date.now())
            )
              return jsonResponse({ ...raw, state: "expired", outcome: null });
            return jsonResponse(result);
          }
          return jsonResponse({
            ...raw,
            state:
              identity.transaction_id.expires_at_unix_ms <= BigInt(Date.now())
                ? "expired"
                : "unknown",
            outcome: null,
          });
        });
      }
      const match =
        /^\/collections\/([^/]+)\/documents\/(query|subscriptions)$/u.exec(
          suffix,
        );
      if (match === null) return problemResponse(404, "unknown_route");
      const collection = requireDatabaseUuid(match[1]);
      return await this.#store.exclusive(() => {
        this.#binding(collection);
        return this.#query(collection, match[2] === "subscriptions", raw);
      });
    } catch (error) {
      if (error instanceof NativeLocalError)
        return problemResponse(error.status, error.code);
      return problemResponse(400, "invalid_request");
    }
  }

  /**
   * @param {string} collection
   * @returns {JsonObject}
   */
  #binding(collection) {
    const value = /** @type {JsonObject | undefined} */ (
      this.#store.get("native-catalog", this.#scopeKey, collection)
    );
    if (value === undefined) throw new NativeLocalError(409, "schema_changed");
    return value;
  }
  /**
   * @param {string} collection
   * @param {string} document
   * @returns {WirePoint}
   */
  #point(collection, document) {
    return (
      /** @type {WirePoint | undefined} */ (
        this.#store.get(
          "native-points",
          `${this.#scopeKey}/${collection}`,
          document,
        )
      ) ?? absent
    );
  }
  /** @returns {bigint} */
  #sequence() {
    return BigInt(
      /** @type {string | undefined} */ (
        this.#store.getMeta(`native-sequence:${this.#scopeKey}`)
      ) ?? "0",
    );
  }

  /**
   * @param {JsonObject} raw
   * @param {DatabaseCommit} commit
   * @returns {Response}
   */
  #commit(raw, commit) {
    const key = transactionKey(raw);
    const previous = /** @type {JsonObject | undefined} */ (
      this.#store.get("native-transactions", this.#scopeKey, key)
    );
    if (previous !== undefined) {
      if (
        previous.effect_digest !== raw.effect_digest ||
        JSON.stringify(previous.authority) !== JSON.stringify(raw.authority)
      )
        return problemResponse(409, "idempotency_conflict");
      if (commit.transaction_id.expires_at_unix_ms <= BigInt(Date.now()))
        return problemResponse(409, "idempotency_expired");
      return previous.state === "rejected"
        ? problemResponse(412, "precondition_failed")
        : jsonResponse(object(previous.outcome).receipt);
    }
    if (commit.transaction_id.expires_at_unix_ms <= BigInt(Date.now()))
      return problemResponse(409, "idempotency_expired");
    if (
      commit.authority.mode === "single_writer" &&
      JSON.stringify(encodeDatabaseAuthority(commit.authority)) !==
        JSON.stringify(encodeDatabaseAuthority(this.authority))
    ) {
      return problemResponse(409, "authority_changed");
    }
    for (const write of commit.writes) {
      const binding = this.#binding(write.collection);
      if (
        binding.schema_digest !== write.schema_digest ||
        binding.schema_generation !== write.schema_generation.toString()
      )
        return problemResponse(409, "schema_changed");
      const point = this.#point(write.collection, write.document);
      if (
        (write.condition.kind === "absent" && point.state !== "absent") ||
        (write.condition.kind === "version" &&
          point.state_version !== write.condition.version)
      ) {
        this.#store.put("native-transactions", this.#scopeKey, key, {
          version: 1,
          transaction_id: raw.transaction_id,
          authority: raw.authority,
          effect_digest: raw.effect_digest,
          state: "rejected",
          outcome: { error: { code: "precondition_failed" } },
        });
        return problemResponse(412, "precondition_failed");
      }
    }
    const sequence = this.#sequence() + 1n;
    const event = {
      database: this.#scope.database,
      shard: this.#scope.shard.toString(),
      ...this.#writer,
      sequence: sequence.toString(),
    };
    /** @type {Change["entries"][number][]} */
    const entries = [];
    for (const write of commit.writes) {
      const before = this.#point(write.collection, write.document);
      const point = decodeDatabasePoint(before);
      const retained = point.siblings.filter(
        (sibling) =>
          !write.observed.some(
            (observed) =>
              observed.replica === sibling.event.replica &&
              observed.incarnation === sibling.event.incarnation &&
              observed.sequence >= sibling.event.sequence,
          ),
      );
      const sibling = {
        event,
        schema_generation: write.schema_generation.toString(),
        schema_digest: write.schema_digest,
        ...wireWriteValue(write.value),
      };
      const siblings = [...retained.map(wireSibling), sibling];
      const context = new Map(
        point.context.map((observed) => [
          observed.replica + observed.incarnation,
          observed,
        ]),
      );
      const own = { ...this.#writer, sequence };
      context.set(own.replica + own.incarnation, own);
      for (const observed of write.observed) {
        const old = context.get(observed.replica + observed.incarnation);
        if (old === undefined || old.sequence < observed.sequence)
          context.set(observed.replica + observed.incarnation, observed);
      }
      const after = {
        version: 1,
        state:
          siblings.length > 1
            ? "conflict"
            : write.value.kind === "live"
              ? "value"
              : "deleted",
        state_version:
          siblings.length > 1
            ? null
            : createHash("sha256")
                .update(JSON.stringify(sibling))
                .digest("hex"),
        context: [...context.values()]
          .sort((a, b) =>
            (a.replica + a.incarnation).localeCompare(
              b.replica + b.incarnation,
            ),
          )
          .map((observed) => ({
            ...observed,
            sequence: observed.sequence.toString(),
          })),
        siblings,
      };
      decodeDatabasePoint(after);
      this.#store.put(
        "native-points",
        `${this.#scopeKey}/${write.collection}`,
        write.document,
        after,
      );
      entries.push({
        collection: write.collection,
        document: write.document,
        before,
        after,
      });
    }
    const receipt = {
      version: 1,
      durability: "local_durable",
      transaction_id: raw.transaction_id,
      authority: raw.authority,
      effect_digest: raw.effect_digest,
      event,
      commit_sequence: sequence.toString(),
    };
    this.#store.put("native-transactions", this.#scopeKey, key, {
      version: 1,
      transaction_id: raw.transaction_id,
      authority: raw.authority,
      effect_digest: raw.effect_digest,
      state: "committed",
      outcome: { receipt },
    });
    this.#store.put(
      "native-changes",
      this.#scopeKey,
      sequence.toString().padStart(20, "0"),
      { sequence: sequence.toString(), entries },
    );
    this.#store.setMeta(
      `native-sequence:${this.#scopeKey}`,
      sequence.toString(),
    );
    return jsonResponse(receipt);
  }

  /**
   * @param {JsonObject} payload
   * @returns {string}
   */
  #cursor(payload) {
    const value = Buffer.from(JSON.stringify(payload)).toString("base64url");
    const signed = `${value}_${createHmac("sha256", this.#secret).update(value).digest("base64url")}`;
    return Buffer.from(signed).toString("base64url");
  }
  /**
   * @param {unknown} value
   * @param {string} route
   * @param {string} predicate
   * @returns {JsonObject}
   */
  #cursorPayload(value, route, predicate) {
    if (typeof value !== "string" || value.length > 1024)
      throw new NativeLocalError(400, "invalid_cursor");
    const signed = Buffer.from(value, "base64url").toString("utf8");
    if (Buffer.from(signed).toString("base64url") !== value)
      throw new NativeLocalError(400, "invalid_cursor");
    const separator = signed.length - 44;
    if (separator < 1 || signed[separator] !== "_")
      throw new NativeLocalError(400, "invalid_cursor");
    const body = signed.slice(0, separator);
    const signature = signed.slice(separator + 1);
    const expected = createHmac("sha256", this.#secret)
      .update(body)
      .digest("base64url");
    if (
      signature.length !== expected.length ||
      !timingSafeEqual(Buffer.from(signature), Buffer.from(expected))
    )
      throw new NativeLocalError(400, "invalid_cursor");
    const payload = object(
      JSON.parse(Buffer.from(body, "base64url").toString("utf8")),
    );
    if (payload.route !== route || payload.predicate !== predicate)
      throw new NativeLocalError(400, "invalid_cursor");
    if (typeof payload.expires !== "number" || payload.expires <= Date.now())
      throw new NativeLocalError(409, "resync_required");
    return payload;
  }
  /**
   * @param {string} collection
   * @param {boolean} subscription
   * @param {JsonObject} raw
   * @returns {Response}
   */
  #query(collection, subscription, raw) {
    for (const entry of /** @type {readonly { readonly scope: string; readonly id: string; readonly value: Snapshot }[]} */ (
      this.#store.listNamespace("native-snapshots")
    ))
      if (entry.value.expires <= Date.now())
        this.#store.delete("native-snapshots", entry.scope, entry.id);
    const predicate = decodePredicate(raw.predicate);
    const predicateKey = createHash("sha256")
      .update(JSON.stringify(raw.predicate))
      .digest("hex");
    const binding = this.#binding(collection);
    const route = `${collection}/${subscription ? "subscription" : "query"}/${binding.schema_generation}/${binding.schema_digest}`;
    if (subscription && raw.phase === "poll") {
      encodeDatabaseSubscription({
        phase: "poll",
        predicate,
        poll_cursor: text(raw.poll_cursor),
      });
      exactKeys(raw, ["version", "phase", "predicate", "poll_cursor"]);
      const cursor = this.#cursorPayload(raw.poll_cursor, route, predicateKey);
      const after = decimal(cursor.sequence);
      const changes = /** @type {Change[]} */ (
        this.#store.list("native-changes", this.#scopeKey)
      )
        .filter((change) => BigInt(change.sequence) > after)
        .slice(0, 8);
      const groups = changes.map((change) => ({
        sequence: change.sequence,
        effects: change.entries.flatMap(
          /** @returns {{ document: string; point: JsonObject | null }[]} */
          (entry) => {
            if (entry.collection !== collection) return [];
            const before = matchesPoint(entry.before, predicate);
            const after = matchesPoint(entry.after, predicate);
            return after
              ? [{ document: entry.document, point: entry.after }]
              : before
                ? [{ document: entry.document, point: null }]
                : [];
          },
        ),
      }));
      if (groups.some((group) => group.effects.length > 128))
        throw new NativeLocalError(409, "resync_required");
      const sequence = changes.at(-1)?.sequence ?? cursor.sequence;
      return jsonResponse({
        version: 1,
        phase: "poll",
        groups,
        poll_cursor: this.#cursor({
          route,
          predicate: predicateKey,
          sequence,
          expires: Date.now() + 60_000,
        }),
      });
    }
    const limit = raw.limit;
    if (typeof limit !== "number") throw new TypeError("Missing limit.");
    if (subscription) {
      encodeDatabaseSubscription({
        phase: "snapshot",
        predicate,
        limit,
        ...(raw.snapshot_cursor === undefined
          ? {}
          : { snapshot_cursor: text(raw.snapshot_cursor) }),
      });
      exactKeys(
        raw,
        ["version", "phase", "predicate", "limit"],
        ["snapshot_cursor"],
      );
      if (raw.phase !== "snapshot") throw new TypeError("Invalid phase.");
    } else {
      encodeDatabaseQuery({
        predicate,
        limit,
        ...(raw.cursor === undefined ? {} : { cursor: text(raw.cursor) }),
      });
      exactKeys(raw, ["version", "predicate", "limit"], ["cursor"]);
    }
    if (raw.version !== 1) throw new TypeError("Invalid version.");
    const cursorValue = subscription ? raw.snapshot_cursor : raw.cursor;
    /** @type {Snapshot} */
    let snapshot;
    /** @type {string} */
    let id;
    let offset = 0;
    if (cursorValue === undefined) {
      id = randomUUID();
      const rows = /** @type {readonly { readonly scope: string; readonly id: string; readonly value: WirePoint }[]} */ (
        this.#store.listNamespace("native-points")
      )
        .filter(
          (entry) =>
            entry.scope === `${this.#scopeKey}/${collection}` &&
            matchesPoint(entry.value, predicate),
        )
        .map((entry) => ({ document: entry.id, point: entry.value }));
      snapshot = {
        route,
        predicate: predicateKey,
        limit,
        sequence: this.#sequence().toString(),
        rows,
        expires: Date.now() + 60_000,
      };
      this.#store.put("native-snapshots", this.#scopeKey, id, snapshot);
    } else {
      const cursor = this.#cursorPayload(cursorValue, route, predicateKey);
      id = text(cursor.id);
      offset = typeof cursor.offset === "number" ? cursor.offset : -1;
      const existing = /** @type {Snapshot | undefined} */ (
        this.#store.get("native-snapshots", this.#scopeKey, id)
      );
      if (
        existing === undefined ||
        existing.limit !== limit ||
        existing.expires <= Date.now() ||
        !Number.isSafeInteger(offset) ||
        offset < 0
      )
        throw new NativeLocalError(409, "resync_required");
      snapshot = existing;
    }
    /** @type {Snapshot["rows"][number][]} */
    const rows = [];
    // Signed cursor/envelope metadata is below 2 KiB. Count each row once;
    // repeated serialization of a large discarded suffix would be quadratic.
    let bytes = 2048;
    for (const row of snapshot.rows.slice(offset, offset + limit)) {
      const rowBytes = Buffer.byteLength(JSON.stringify(row)) + 1;
      if (bytes + rowBytes > maximumBodyBytes) break;
      bytes += rowBytes;
      rows.push(row);
    }
    if (rows.length === 0 && offset < snapshot.rows.length)
      throw new NativeLocalError(503, "capacity");
    const next = offset + rows.length;
    const continuation =
      next < snapshot.rows.length
        ? {
            [subscription ? "next_snapshot_cursor" : "next_cursor"]:
              this.#cursor({
                route,
                predicate: predicateKey,
                id,
                offset: next,
                expires: snapshot.expires,
              }),
          }
        : subscription
          ? {
              poll_cursor: this.#cursor({
                route,
                predicate: predicateKey,
                sequence: snapshot.sequence,
                expires: snapshot.expires,
              }),
            }
          : {};
    return jsonResponse({
      version: 1,
      ...(subscription ? { phase: "snapshot" } : {}),
      rows,
      ...continuation,
    });
  }
}

class NativeLocalError extends Error {
  /**
   * @readonly
   * @type {number}
   */
  status;
  /**
   * @readonly
   * @type {string}
   */
  code;

  /**
   * @param {number} status
   * @param {string} code
   */
  constructor(status, code) {
    super(code);
    this.status = status;
    this.code = code;
  }
}
/**
 * @param {unknown} value
 * @returns {JsonObject}
 */
function object(value) {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    throw new TypeError("Expected object.");
  return /** @type {JsonObject} */ (value);
}
/**
 * @param {unknown} value
 * @returns {string}
 */
function text(value) {
  if (typeof value !== "string") throw new TypeError("Expected string.");
  return value;
}
/**
 * @param {unknown} value
 * @returns {bigint}
 */
function decimal(value) {
  const input = text(value);
  if (!/^(0|[1-9][0-9]*)$/u.test(input))
    throw new TypeError("Expected decimal.");
  return requireDatabaseU64(BigInt(input));
}
/**
 * @param {JsonObject} value
 * @param {readonly string[]} required
 * @param {readonly string[]} [optional]
 * @returns {void}
 */
function exactKeys(value, required, optional = []) {
  if (
    required.some((key) => !Object.hasOwn(value, key)) ||
    Object.keys(value).some(
      (key) => !required.includes(key) && !optional.includes(key),
    )
  )
    throw new TypeError("Unexpected fields.");
}
/**
 * @param {JsonObject} raw
 * @returns {string}
 */
function transactionKey(raw) {
  const id = object(raw.transaction_id);
  return `${text(id.expires_at_unix_ms)}:${text(id.nonce)}`;
}
/**
 * @param {unknown} value
 * @returns {readonly DatabaseField[]}
 */
function decodeFields(value) {
  if (!Array.isArray(value)) throw new TypeError("Invalid fields.");
  return value.map((raw) => {
    const field = object(raw);
    exactKeys(field, ["id", "value"]);
    if (typeof field.id !== "number") throw new TypeError("Invalid field ID.");
    return { id: field.id, value: decodeDatabaseValue(field.value) };
  });
}
/**
 * @param {JsonObject} raw
 * @returns {DatabaseCommit}
 */
function decodeCommit(raw) {
  exactKeys(raw, [
    "version",
    "transaction_id",
    "authority",
    "effect_digest",
    "writes",
  ]);
  if (raw.version !== 1 || !Array.isArray(raw.writes))
    throw new TypeError("Invalid commit.");
  const identity = object(raw.transaction_id);
  exactKeys(identity, ["expires_at_unix_ms", "nonce"]);
  return {
    transaction_id: {
      expires_at_unix_ms: decimal(identity.expires_at_unix_ms),
      nonce: text(identity.nonce),
    },
    authority: decodeDatabaseAuthority(raw.authority),
    writes: raw.writes.map((rawWrite) => {
      const write = object(rawWrite);
      const value = object(write.value);
      exactKeys(write, [
        "collection",
        "schema_generation",
        "schema_digest",
        "document",
        "condition",
        "observed",
        "value",
      ]);
      const condition = object(write.condition);
      exactKeys(
        condition,
        condition.kind === "version" ? ["kind", "version"] : ["kind"],
      );
      exactKeys(value, value.kind === "live" ? ["kind", "fields"] : ["kind"]);
      const parsedCondition =
        condition.kind === "version"
          ? { kind: "version", version: text(condition.version) }
          : { kind: condition.kind };
      if (!Array.isArray(write.observed))
        throw new TypeError("Invalid observed.");
      return {
        collection: requireDatabaseUuid(write.collection),
        schema_generation: decimal(write.schema_generation),
        schema_digest: text(write.schema_digest),
        document: requireDatabaseUuid(write.document),
        condition: /** @type {DatabaseCondition} */ (parsedCondition),
        observed: write.observed.map((rawObserved) => {
          const observed = object(rawObserved);
          exactKeys(observed, ["replica", "incarnation", "sequence"]);
          return {
            replica: text(observed.replica),
            incarnation: text(observed.incarnation),
            sequence: decimal(observed.sequence),
          };
        }),
        value:
          value.kind === "live"
            ? { kind: "live", fields: decodeFields(value.fields) }
            : /** @type {DatabaseWrite["value"]} */ ({ kind: value.kind }),
      };
    }),
  };
}
/**
 * @param {DatabaseWrite["value"]} value
 * @returns {JsonObject}
 */
function wireWriteValue(value) {
  return value.kind === "tombstone"
    ? { kind: "tombstone" }
    : {
        kind: "live",
        fields: value.fields.map((field) => ({
          id: field.id,
          value: encodeDatabaseValue(field.value),
        })),
      };
}
/**
 * @param {DatabasePoint["siblings"][number]} sibling
 * @returns {JsonObject}
 */
function wireSibling(sibling) {
  return {
    event: {
      ...sibling.event,
      shard: sibling.event.shard.toString(),
      sequence: sibling.event.sequence.toString(),
    },
    schema_generation: sibling.schema_generation.toString(),
    schema_digest: sibling.schema_digest,
    ...wireWriteValue(sibling),
  };
}
/**
 * @param {unknown} raw
 * @returns {DatabaseQueryPredicate}
 */
function decodePredicate(raw) {
  const value = object(raw);
  if (!Array.isArray(value.path)) throw new TypeError("Invalid path.");
  const predicate = /** @type {DatabaseQueryPredicate} */ ({
    ...value,
    ...(value.value === undefined
      ? {}
      : { value: decodeDatabaseValue(value.value) }),
  });
  encodeDatabaseQuery({ predicate, limit: 1 });
  return predicate;
}
/**
 * @param {WirePoint} raw
 * @param {DatabaseQueryPredicate} predicate
 * @returns {boolean}
 */
function matchesPoint(raw, predicate) {
  const point = decodeDatabasePoint(raw);
  return point.siblings.some((sibling) => {
    if (sibling.kind !== "live") return false;
    let fields = sibling.fields;
    /** @type {DatabaseValue | undefined} */
    let value;
    for (const [index, id] of predicate.path.entries()) {
      value = fields.find((field) => field.id === id)?.value;
      if (index < predicate.path.length - 1) {
        if (value?.type !== "map") {
          value = undefined;
          break;
        }
        fields = value.fields;
      }
    }
    if (predicate.op === "is_missing") return value === undefined;
    if (predicate.op === "is_null") return value?.type === "null";
    if (!("value" in predicate)) return false;
    if (
      value === undefined ||
      value.type !== predicate.value.type ||
      !["bool", "i64", "utf8", "bytes"].includes(value.type)
    )
      return false;
    const left = /** @type {Extract<DatabaseValue, { type: "bool" | "i64" | "utf8" | "bytes" }>} */ (
      value
    ).value;
    const right = predicate.value.value;
    const comparison =
      left instanceof Uint8Array && right instanceof Uint8Array
        ? Buffer.compare(left, right)
        : left === right
          ? 0
          : left < right
            ? -1
            : 1;
    switch (predicate.op) {
      case "equal":
        return comparison === 0;
      case "less_than":
        return comparison < 0;
      case "less_than_or_equal":
        return comparison <= 0;
      case "greater_than":
        return comparison > 0;
      case "greater_than_or_equal":
        return comparison >= 0;
    }
  });
}
/**
 * @param {Request} request
 * @returns {Promise<unknown>}
 */
async function readBody(request) {
  const reader = request.body?.getReader();
  if (reader === undefined) throw new TypeError("Missing body.");
  /** @type {Uint8Array[]} */
  const parts = [];
  let size = 0;
  try {
    while (true) {
      request.signal.throwIfAborted();
      const chunk = await awaitAbortable(reader.read(), request.signal);
      if (chunk.done) break;
      size += chunk.value.byteLength;
      if (size > maximumBodyBytes) throw new TypeError("Body limit exceeded.");
      parts.push(chunk.value);
    }
  } catch (error) {
    void reader.cancel().catch(() => undefined);
    throw error;
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const part of parts) {
    bytes.set(part, offset);
    offset += part.byteLength;
  }
  return parseDatabaseJson(bytes);
}

/**
 * @template T
 * @param {Promise<T>} work
 * @param {AbortSignal} signal
 * @returns {Promise<T>}
 */
async function awaitAbortable(work, signal) {
  if (signal.aborted) {
    void work.catch(() => undefined);
    throw signal.reason;
  }
  /** @type {() => void} */
  let onAbort = () => {};
  /** @type {Promise<never>} */
  const aborted = new Promise((_, reject) => {
    onAbort = () => reject(signal.reason);
    signal.addEventListener("abort", onAbort, { once: true });
    if (signal.aborted) onAbort();
  });
  try {
    return await Promise.race([work, aborted]);
  } finally {
    signal.removeEventListener("abort", onAbort);
  }
}
