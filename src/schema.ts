import { decodeDatabaseValue, encodeDatabaseValue } from "./database-codec.js";
import type { DatabaseField, DatabaseValue } from "./database-types.js";

/** Pure local declarations. These are not server catalog or migration requests. */
export const FieldTypes = Object.freeze({
  bool: "bool",
  i64: "i64",
  utf8: "utf8",
  bytes: "bytes",
  map: "map",
  array: "array",
} as const);
export type FieldType = (typeof FieldTypes)[keyof typeof FieldTypes];
export type FieldOptions = { readonly required?: boolean };
export type FieldDef = {
  readonly id: number;
  readonly name: string;
  readonly type: FieldType;
  /** Required refers to field presence; explicit native null remains distinct. */
  readonly required: boolean;
};
export type CollectionSchema = {
  readonly name: string;
  readonly fields: readonly FieldDef[];
};
export type SchemaJson = { readonly collections: readonly CollectionSchema[] };
export type CollectionOptions = { readonly fields?: readonly FieldDef[] };
const types = new Set<string>(Object.values(FieldTypes));
const identifier = /^[A-Za-z_][A-Za-z0-9_]*$/u;

export class SchemaValidationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SchemaValidationError";
  }
}

/** Field IDs must come from the application's reviewed collection binding. */
export function defineField(
  id: number,
  name: string,
  type: FieldType,
  options: FieldOptions = {},
): FieldDef {
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
  readonly #name: string;
  readonly #fields: readonly FieldDef[];
  constructor(name: string, options: CollectionOptions = {}) {
    assertName(name, "collection");
    this.#name = name;
    this.#fields = Object.freeze(
      (options.fields ?? []).map((value) =>
        defineField(value.id, value.name, value.type, value),
      ),
    );
  }
  field(
    id: number,
    name: string,
    type: FieldType,
    options: FieldOptions = {},
  ): CollectionBuilder {
    return new CollectionBuilder(this.#name, {
      fields: [...this.#fields, defineField(id, name, type, options)],
    });
  }
  build(): CollectionSchema {
    return normalizeCollection({ name: this.#name, fields: this.#fields });
  }
  toJSON(): string {
    return serializeSchema({ collections: [this.build()] });
  }
}
export function collection(
  name: string,
  options: CollectionOptions = {},
): CollectionBuilder {
  return new CollectionBuilder(name, options);
}

export class SchemaBuilder {
  readonly #entries: readonly CollectionSchema[];
  constructor(entries: readonly (CollectionSchema | CollectionBuilder)[] = []) {
    this.#entries = Object.freeze(
      entries.map((entry) =>
        entry instanceof CollectionBuilder
          ? entry.build()
          : normalizeCollection(entry),
      ),
    );
  }
  addCollection(entry: CollectionSchema | CollectionBuilder): SchemaBuilder {
    return new SchemaBuilder([...this.#entries, entry]);
  }
  collection(name: string, options: CollectionOptions = {}): SchemaBuilder {
    return this.addCollection(collection(name, options));
  }
  build(): SchemaJson {
    return normalizeSchema({ collections: this.#entries });
  }
  validate(): void {
    validateSchema(this.build());
  }
  toJSON(): string {
    return serializeSchema(this.build());
  }
}
export function defineSchema(
  entries: readonly (CollectionSchema | CollectionBuilder)[],
): SchemaBuilder {
  return new SchemaBuilder(entries);
}
export function serializeSchema(schema: SchemaJson | SchemaBuilder): string {
  return JSON.stringify(
    schema instanceof SchemaBuilder ? schema.build() : normalizeSchema(schema),
  );
}
export function normalizeSchema(schema: SchemaJson): SchemaJson {
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
export function validateSchema(schema: SchemaJson): void {
  normalizeSchema(schema);
}
export function validateCollection(schema: CollectionSchema): void {
  normalizeCollection(schema);
}

function normalizeCollection(schema: CollectionSchema): CollectionSchema {
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

/** Validate native values using the accepted codec; never coerce integers or bytes. */
export function encodeSchemaFields(
  schema: CollectionSchema,
  record: Readonly<Record<string, DatabaseValue>>,
): readonly DatabaseField[] {
  const declaration = normalizeCollection(schema);
  const declared = new Set(declaration.fields.map((field) => field.name));
  for (const key of Object.keys(record))
    if (!declared.has(key))
      throw new SchemaValidationError(`Undeclared field ${key}.`);
  const fields: DatabaseField[] = [];
  for (const field of declaration.fields) {
    if (!Object.hasOwn(record, field.name)) {
      if (field.required)
        throw new SchemaValidationError(`Missing field ${field.name}.`);
      continue;
    }
    const value = record[field.name]!;
    if (value.type !== "null" && value.type !== field.type)
      throw new SchemaValidationError(
        `Field ${field.name} requires ${field.type}.`,
      );
    fields.push({ id: field.id, value });
  }
  let nodes = 0;
  const size = (value: DatabaseValue, depth: number): number => {
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

function assertName(value: string, kind: string): void {
  if (
    typeof value !== "string" ||
    !identifier.test(value) ||
    value.length > 255
  )
    throw new SchemaValidationError(
      `${kind} name must be a bounded identifier.`,
    );
}
