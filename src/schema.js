import { decodeDatabaseValue, encodeDatabaseValue } from "./database-codec.js";
/** @import { DatabaseField, DatabaseValue } from "./database-types.js" */

/** Pure local declarations. These are not server catalog or migration requests. */
export const FieldTypes = Object.freeze(
  /** @type {const} */ ({
    bool: "bool",
    i64: "i64",
    utf8: "utf8",
    bytes: "bytes",
    map: "map",
    array: "array",
  }),
);
/** @typedef {(typeof FieldTypes)[keyof typeof FieldTypes]} FieldType */
/** @typedef {{ readonly required?: boolean }} FieldOptions */
/**
 * `required` refers to field presence; explicit native null remains distinct.
 *
 * @typedef {{
 *   readonly id: number;
 *   readonly name: string;
 *   readonly type: FieldType;
 *   readonly required: boolean;
 * }} FieldDef
 */
/**
 * @typedef {{
 *   readonly name: string;
 *   readonly fields: readonly FieldDef[];
 * }} CollectionSchema
 */
/** @typedef {{ readonly collections: readonly CollectionSchema[] }} SchemaJson */
/** @typedef {{ readonly fields?: readonly FieldDef[] }} CollectionOptions */
/** @type {Set<string>} */
const types = new Set(Object.values(FieldTypes));
const identifier = /^[A-Za-z_][A-Za-z0-9_]*$/u;

export class SchemaValidationError extends Error {
  /**
   * @param {string} message
   */
  constructor(message) {
    super(message);
    this.name = "SchemaValidationError";
  }
}

/**
 * Field IDs must come from the application's reviewed collection binding.
 *
 * @param {number} id
 * @param {string} name
 * @param {FieldType} type
 * @param {FieldOptions} [options]
 * @returns {FieldDef}
 */
export function defineField(id, name, type, options = {}) {
  if (!Number.isInteger(id) || id < 1 || id > 0xffffffff)
    throw new SchemaValidationError("Field IDs must be positive u32 values.");
  assertName(name, "field");
  if (!types.has(type))
    throw new SchemaValidationError(
      `Unsupported native field type ${String(type)}.`,
    );
  if (options.required !== undefined && typeof options.required !== "boolean")
    throw new SchemaValidationError("required must be boolean.");
  return Object.freeze({
    id,
    name,
    type,
    required: options.required !== false,
  });
}

/** Persistent fluent builder; each branch keeps its own declarations. */
export class CollectionBuilder {
  /** @type {string} */
  #name;
  /** @type {readonly FieldDef[]} */
  #fields;
  /**
   * @param {string} name
   * @param {CollectionOptions} [options]
   */
  constructor(name, options = {}) {
    assertName(name, "collection");
    this.#name = name;
    this.#fields = Object.freeze(
      (options.fields ?? []).map((value) =>
        defineField(value.id, value.name, value.type, value),
      ),
    );
  }
  /**
   * @param {number} id
   * @param {string} name
   * @param {FieldType} type
   * @param {FieldOptions} [options]
   * @returns {CollectionBuilder}
   */
  field(id, name, type, options = {}) {
    return new CollectionBuilder(this.#name, {
      fields: [...this.#fields, defineField(id, name, type, options)],
    });
  }
  /**
   * @returns {CollectionSchema}
   */
  build() {
    return normalizeCollection({ name: this.#name, fields: this.#fields });
  }
  /**
   * @returns {string}
   */
  toJSON() {
    return serializeSchema({ collections: [this.build()] });
  }
}
/**
 * @param {string} name
 * @param {CollectionOptions} [options]
 * @returns {CollectionBuilder}
 */
export function collection(name, options = {}) {
  return new CollectionBuilder(name, options);
}

export class SchemaBuilder {
  /** @type {readonly CollectionSchema[]} */
  #entries;
  /**
   * @param {readonly (CollectionSchema | CollectionBuilder)[]} [entries]
   */
  constructor(entries = []) {
    this.#entries = Object.freeze(
      entries.map((entry) =>
        entry instanceof CollectionBuilder
          ? entry.build()
          : normalizeCollection(entry),
      ),
    );
  }
  /**
   * @param {CollectionSchema | CollectionBuilder} entry
   * @returns {SchemaBuilder}
   */
  addCollection(entry) {
    return new SchemaBuilder([...this.#entries, entry]);
  }
  /**
   * @param {string} name
   * @param {CollectionOptions} [options]
   * @returns {SchemaBuilder}
   */
  collection(name, options = {}) {
    return this.addCollection(collection(name, options));
  }
  /**
   * @returns {SchemaJson}
   */
  build() {
    return normalizeSchema({ collections: this.#entries });
  }
  /**
   * @returns {void}
   */
  validate() {
    validateSchema(this.build());
  }
  /**
   * @returns {string}
   */
  toJSON() {
    return serializeSchema(this.build());
  }
}
/**
 * @param {readonly (CollectionSchema | CollectionBuilder)[]} entries
 * @returns {SchemaBuilder}
 */
export function defineSchema(entries) {
  return new SchemaBuilder(entries);
}
/**
 * @param {SchemaJson | SchemaBuilder} schema
 * @returns {string}
 */
export function serializeSchema(schema) {
  return JSON.stringify(
    schema instanceof SchemaBuilder ? schema.build() : normalizeSchema(schema),
  );
}
/**
 * @param {SchemaJson} schema
 * @returns {SchemaJson}
 */
export function normalizeSchema(schema) {
  if (
    schema === null ||
    typeof schema !== "object" ||
    !Array.isArray(schema.collections) ||
    Object.keys(schema).some((key) => key !== "collections")
  ) {
    throw new SchemaValidationError(
      "Local schema requires only a collections array.",
    );
  }
  const entries = schema.collections
    .map(normalizeCollection)
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  if (new Set(entries.map((entry) => entry.name)).size !== entries.length)
    throw new SchemaValidationError("Duplicate collection name.");
  return Object.freeze({ collections: Object.freeze(entries) });
}
/**
 * @param {SchemaJson} schema
 * @returns {void}
 */
export function validateSchema(schema) {
  normalizeSchema(schema);
}
/**
 * @param {CollectionSchema} schema
 * @returns {void}
 */
export function validateCollection(schema) {
  normalizeCollection(schema);
}

/**
 * @param {CollectionSchema} schema
 * @returns {CollectionSchema}
 */
function normalizeCollection(schema) {
  if (
    schema === null ||
    typeof schema !== "object" ||
    !Array.isArray(schema.fields) ||
    Object.keys(schema).some((key) => key !== "name" && key !== "fields")
  ) {
    throw new SchemaValidationError(
      "Local collection requires only name and fields.",
    );
  }
  assertName(schema.name, "collection");
  if (schema.fields.length > 1024)
    throw new SchemaValidationError(
      "A declaration permits at most 1024 fields.",
    );
  const fields = schema.fields
    .map((value) => {
      if (
        Object.keys(value).some(
          (key) => !["id", "name", "type", "required"].includes(key),
        )
      )
        throw new SchemaValidationError(
          "Unknown local field declaration property.",
        );
      return defineField(value.id, value.name, value.type, value);
    })
    .sort((a, b) => a.id - b.id);
  if (
    new Set(fields.map((value) => value.id)).size !== fields.length ||
    new Set(fields.map((value) => value.name)).size !== fields.length
  ) {
    throw new SchemaValidationError("Field names and IDs must be unique.");
  }
  return Object.freeze({ name: schema.name, fields: Object.freeze(fields) });
}

/**
 * Validate native values using the accepted codec; never coerce integers or bytes.
 *
 * @param {CollectionSchema} schema
 * @param {Readonly<Record<string, DatabaseValue>>} record
 * @returns {readonly DatabaseField[]}
 */
export function encodeSchemaFields(schema, record) {
  const declaration = normalizeCollection(schema);
  const declared = new Set(declaration.fields.map((field) => field.name));
  for (const key of Object.keys(record))
    if (!declared.has(key))
      throw new SchemaValidationError(`Undeclared field ${key}.`);
  /** @type {DatabaseField[]} */
  const fields = [];
  for (const field of declaration.fields) {
    if (!Object.hasOwn(record, field.name)) {
      if (field.required)
        throw new SchemaValidationError(`Missing field ${field.name}.`);
      continue;
    }
    const value = /** @type {DatabaseValue} */ (record[field.name]);
    if (value.type !== "null" && value.type !== field.type)
      throw new SchemaValidationError(
        `Field ${field.name} requires ${field.type}.`,
      );
    fields.push({ id: field.id, value });
  }
  let nodes = 0;
  /**
   * @param {DatabaseValue} value
   * @param {number} depth
   * @returns {number}
   */
  const size = (value, depth) => {
    if (depth > 16 || ++nodes > 4096)
      throw new SchemaValidationError("Document exceeds depth/node bounds.");
    switch (value.type) {
      case "null":
        return 0;
      case "bool":
        return 1;
      case "i64":
        return 8;
      case "utf8":
        return new TextEncoder().encode(value.value).byteLength;
      case "bytes":
        return value.value.byteLength;
      case "map":
        return (
          2 +
          value.fields.reduce(
            (total, field) => total + 9 + size(field.value, depth + 1),
            0,
          )
        );
      case "array":
        return (
          2 +
          value.items.reduce(
            (total, item) => total + 5 + size(item, depth + 1),
            0,
          )
        );
    }
  };
  let documentSize = 98 + 13 * fields.length;
  const result = fields.map((field) => {
    const value = decodeDatabaseValue(encodeDatabaseValue(field.value));
    documentSize += size(value, 1);
    return { id: field.id, value };
  });
  if (documentSize > 1_048_576)
    throw new SchemaValidationError(
      "Document exceeds its 1 MiB encoded bound.",
    );
  return result;
}

/**
 * @param {string} value
 * @param {string} kind
 * @returns {void}
 */
function assertName(value, kind) {
  if (
    typeof value !== "string" ||
    !identifier.test(value) ||
    value.length > 255
  )
    throw new SchemaValidationError(
      `${kind} name must be a bounded identifier.`,
    );
}
