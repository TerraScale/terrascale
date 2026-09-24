/**
 * Typed database schema declarations (D-78).
 *
 * The value returned by this module is deliberately ordinary JSON-shaped
 * data. `terrascale.schema.json` is the contract consumed by push and codegen;
 * the builder only adds validation and deterministic serialization around it.
 */

export const FIELD_TYPES = {
  string: "string",
  int: "int",
  integer: "int",
  float: "float",
  bool: "bool",
  boolean: "bool",
  bytes: "bytes",
  json: "json",
  timestamp: "timestamp",
  text: "text",
  counter: "counter-pn",
  counterPn: "counter-pn",
  setAddWins: "set-add-wins",
  setRemoveWins: "set-remove-wins",
  registerMv: "register-mv",
} as const;
export const FieldTypes = FIELD_TYPES;
export const FieldString = FIELD_TYPES.string;
export const FieldInt = FIELD_TYPES.int;
export const FieldInteger = FIELD_TYPES.int;
export const FieldFloat = FIELD_TYPES.float;
export const FieldBool = FIELD_TYPES.bool;
export const FieldBoolean = FIELD_TYPES.bool;
export const FieldBytes = FIELD_TYPES.bytes;
export const FieldJSON = FIELD_TYPES.json;
export const FieldTimestamp = FIELD_TYPES.timestamp;
export const FieldText = FIELD_TYPES.text;
export const FieldCounter = FIELD_TYPES.counter;
export const FieldPNCounter = FIELD_TYPES.counterPn;
export const FieldSetAddWins = FIELD_TYPES.setAddWins;
export const FieldAddWinsSet = FIELD_TYPES.setAddWins;
export const FieldSetRemoveWins = FIELD_TYPES.setRemoveWins;
export const FieldRemoveWinsSet = FIELD_TYPES.setRemoveWins;
export const FieldRegisterMV = FIELD_TYPES.registerMv;
export const FieldMVRegister = FIELD_TYPES.registerMv;

export const MERGE_POLICIES = {
  removeWins: "remove-wins",
  hlcTiebreak: "hlc-tiebreak",
  lastWriteWins: "last-write-wins",
} as const;
export const MergePolicies = MERGE_POLICIES;
export const MergeRemoveWins = MERGE_POLICIES.removeWins;
export const MergeHLCTiebreak = MERGE_POLICIES.hlcTiebreak;
export const MergeLastWriteWins = MERGE_POLICIES.lastWriteWins;
export const MergePolicyRemoveWins = MergeRemoveWins;
export const MergePolicyHLCTiebreak = MergeHLCTiebreak;
export const MergePolicyLastWriteWins = MergeLastWriteWins;

export const INDEX_KINDS = { btree: "btree", fts: "fts" } as const;
export const IndexKinds = INDEX_KINDS;

export type FieldType = (typeof FIELD_TYPES)[keyof typeof FIELD_TYPES];
export type ScalarFieldType = "string" | "int" | "float" | "bool" | "bytes" | "json" | "timestamp" | "text";
export type CrdtFieldType = "counter-pn" | "set-add-wins" | "set-remove-wins" | "register-mv";
export type MergePolicy = "remove-wins" | "hlc-tiebreak" | "last-write-wins";
export type IndexKind = "btree" | "fts";

const FIELD_TYPE_SET = new Set<string>(Object.values(FIELD_TYPES));
const CRDT_TYPE_SET = new Set<CrdtFieldType>([
  "counter-pn",
  "set-add-wins",
  "set-remove-wins",
  "register-mv",
]);
const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/u;
const MIN_HISTORY_DAYS = 1;
const MAX_HISTORY_DAYS = 30;
const DEFAULT_HISTORY_DAYS = 7;

export interface FieldDef {
  readonly name: string;
  readonly type: FieldType;
  readonly required: boolean;
  readonly indexed: boolean;
  readonly unique?: boolean;
  readonly mergePolicy?: FieldType;
  readonly fts?: boolean;
}

export interface IndexDef {
  readonly name: string;
  readonly fields: readonly string[];
  readonly unique?: boolean;
  readonly kind?: IndexKind;
}

export interface FtsDef {
  readonly fields: readonly string[];
}

export interface TtlDef {
  readonly field: string;
}

export interface CollectionSchema {
  readonly name: string;
  readonly fields: readonly FieldDef[];
  readonly indexes?: readonly IndexDef[];
  readonly fts?: FtsDef;
  readonly ttl?: TtlDef;
  /** Explicit migration metadata. A rename source is the old field name. */
  readonly renames?: readonly FieldRename[];
  /** Optional schema identity used by resumable migrations. */
  readonly version?: number;
  readonly historyRetentionDays: number;
  readonly mergePolicy: MergePolicy;
}

export interface SchemaJson {
  readonly collections: readonly CollectionSchema[];
  /** Optional document format version; omitted for the initial contract. */
  readonly version?: number;
}

export interface FieldRename {
  readonly from: string;
  readonly to: string;
}

export interface FieldOptions {
  readonly required?: boolean;
  readonly optional?: boolean;
  readonly indexed?: boolean;
  readonly unique?: boolean;
  readonly mergePolicy?: FieldType;
  /** Alias accepted for callers that call a CRDT kernel a merge type. */
  readonly merge?: FieldType;
  readonly fts?: boolean;
}

export interface IndexOptions {
  readonly fields: readonly string[] | string;
  readonly unique?: boolean;
  readonly kind?: IndexKind;
}

export interface CollectionOptions {
  readonly fields?: Readonly<Record<string, FieldDefInput>> | readonly FieldDefInput[];
  readonly indexes?: Readonly<Record<string, IndexOptions | readonly string[]>> | readonly IndexInput[];
  readonly fts?: FtsInput;
  readonly ttl?: string | TtlDef;
  readonly renames?: readonly FieldRename[] | Readonly<Record<string, string>>;
  readonly version?: number;
  readonly schemaVersion?: number;
  readonly historyRetentionDays?: number;
  readonly historyWindowDays?: number;
  readonly history?: number;
  readonly mergePolicy?: MergePolicy;
  readonly merge?: MergePolicy;
}

export type FieldDefInput =
  | FieldDef
  | FieldBuilder
  | FieldType
  | { readonly type: FieldType; readonly required?: boolean; readonly optional?: boolean; readonly indexed?: boolean; readonly unique?: boolean; readonly mergePolicy?: FieldType; readonly merge?: FieldType; readonly fts?: boolean };

export type IndexInput = IndexDef | { readonly name: string; readonly fields: readonly string[] | string; readonly unique?: boolean; readonly kind?: IndexKind };
export type FtsInput = FtsDef | readonly string[] | string;

/** A small field DSL for declarations that should be readable in application code. */
export class FieldBuilder {
  private readonly definition: FieldDef;

  public constructor(type: FieldType, options: FieldOptions = {}) {
    const required = options.optional === true ? false : options.required !== false;
    this.definition = {
      name: "",
      type,
      required,
      indexed: options.indexed === true || options.unique === true,
      ...(options.unique === true ? { unique: true } : {}),
      ...(options.mergePolicy ?? options.merge ? { mergePolicy: options.mergePolicy ?? options.merge } : {}),
      ...(options.fts === true ? { fts: true } : {}),
    };
  }

  public named(name: string): FieldDef {
    return { ...this.definition, name };
  }

  public required(): FieldBuilder {
    return new FieldBuilder(this.definition.type, { ...this.definition, required: true, optional: false });
  }

  public optional(): FieldBuilder {
    return new FieldBuilder(this.definition.type, { ...this.definition, required: false, optional: true });
  }

  public indexed(): FieldBuilder {
    return new FieldBuilder(this.definition.type, { ...this.definition, indexed: true });
  }

  public unique(): FieldBuilder {
    return new FieldBuilder(this.definition.type, { ...this.definition, indexed: true, unique: true });
  }

  public mergePolicy(policy: CrdtFieldType): FieldBuilder {
    return new FieldBuilder(this.definition.type, { ...this.definition, mergePolicy: policy });
  }

  public toDefinition(name = this.definition.name): FieldDef {
    return { ...this.definition, name };
  }
}

export function field(type: FieldType, options: FieldOptions = {}): FieldBuilder {
  assertFieldType(type);
  return new FieldBuilder(type, options);
}
export const string = (options?: FieldOptions) => field("string", options);
export const int = (options?: FieldOptions) => field("int", options);
export const float = (options?: FieldOptions) => field("float", options);
export const bool = (options?: FieldOptions) => field("bool", options);
export const bytes = (options?: FieldOptions) => field("bytes", options);
export const json = (options?: FieldOptions) => field("json", options);
export const timestamp = (options?: FieldOptions) => field("timestamp", options);
export const text = (options?: FieldOptions) => field("text", options);
export const counter = (options?: FieldOptions) => field("counter-pn", options);
export const pnCounter = counter;
export const setAddWins = (options?: FieldOptions) => field("set-add-wins", options);
export const addWinsSet = setAddWins;
export const setRemoveWins = (options?: FieldOptions) => field("set-remove-wins", options);
export const removeWinsSet = setRemoveWins;
export const registerMv = (options?: FieldOptions) => field("register-mv", options);
export const mvRegister = registerMv;
export const integer = int;
export const boolean = bool;

/** Build one field definition for object-style schemas. */
export function defineField(name: string, type: FieldType, options?: FieldOptions): FieldDef {
  return field(type, options).named(name);
}

export function index(name: string, fields: readonly string[] | string, options: { readonly unique?: boolean } = {}): IndexDef {
  return normalizeIndex({ name, fields, ...options, kind: "btree" });
}

export const newIndex = index;
export const NewIndex = index;
export function newFtsIndex(name: string, field: string): IndexDef {
  return normalizeIndex({ name, fields: [field], kind: "fts" });
}
export const NewFTSIndex = newFtsIndex;

export const fullText = (...fields: string[]): FtsDef => ({ fields });
export const fts = fullText;
export const ttl = (field: string): TtlDef => ({ field });

export class CollectionBuilder {
  private readonly options: CollectionOptions;
  private readonly collectionName: string;

  public constructor(name: string, options: CollectionOptions = {}) {
    this.collectionName = name;
    // Builders are persistent from the caller's point of view. In particular,
    // fluent calls must not mutate a schema object that is being reused for a
    // second branch or for a comparison.
    this.options = cloneCollectionOptions(options);
  }

  public field(name: string, type: FieldType, options?: FieldOptions): this {
    const fields = Array.isArray(this.options.fields) ? [...this.options.fields] : Object.entries(this.options.fields ?? {}).map(([fieldName, definition]) => normalizeField(fieldName, definition));
    fields.push(defineField(name, type, options));
    (this.options as { fields?: readonly FieldDefInput[] }).fields = fields;
    return this;
  }

  public index(name: string, fields: readonly string[] | string, unique = false): this {
    const indexes = Array.isArray(this.options.indexes) ? [...this.options.indexes] : [];
    indexes.push({ name, fields: typeof fields === "string" ? [fields] : fields, ...(unique ? { unique: true } : {}) });
    (this.options as { indexes?: readonly IndexInput[] }).indexes = indexes;
    return this;
  }

  public fullText(...fields: string[]): this {
    (this.options as { fts?: FtsInput }).fts = fields;
    return this;
  }

  public fts(...fields: string[]): this {
    return this.fullText(...fields);
  }

  public ttl(fieldName: string): this {
    (this.options as { ttl?: string }).ttl = fieldName;
    return this;
  }

  public historyRetention(days: number): this {
    (this.options as { historyRetentionDays?: number }).historyRetentionDays = days;
    return this;
  }

  public historyWindowDays(days: number): this {
    return this.historyRetention(days);
  }

  public historyRetentionDays(days: number): this {
    return this.historyRetention(days);
  }

  public historyWindow(days: number): this {
    return this.historyRetention(days);
  }

  public mergePolicy(policy: MergePolicy): this {
    (this.options as { mergePolicy?: MergePolicy }).mergePolicy = policy;
    return this;
  }

  public merge(policy: MergePolicy): this {
    return this.mergePolicy(policy);
  }

  public rename(from: string, to: string): this {
    const renames = Array.isArray(this.options.renames) ? [...this.options.renames] : Object.entries(this.options.renames ?? {}).map(([source, destination]) => ({ from: source, to: destination }));
    renames.push({ from, to });
    (this.options as { renames?: readonly FieldRename[] }).renames = renames;
    return this;
  }

  public renames(value: readonly FieldRename[] | Readonly<Record<string, string>>): this {
    (this.options as { renames?: readonly FieldRename[] }).renames = Array.isArray(value) ? [...value] : Object.entries(value).map(([from, to]) => ({ from, to }));
    return this;
  }

  public version(value: number): this {
    (this.options as { version?: number }).version = value;
    return this;
  }

  public schemaVersion(value: number): this {
    return this.version(value);
  }

  public build(): CollectionSchema {
    return normalizeCollection(this.collectionName, this.options);
  }

  public toJSON(): string {
    return serializeSchema({ collections: [this.build()] });
  }

  public toJson(): string {
    return this.toJSON();
  }
}

export function collection(name: string, options: CollectionOptions = {}): CollectionBuilder {
  return new CollectionBuilder(name, options);
}
export const defineCollection = collection;
export const newCollection = collection;

export type SchemaInput =
  | SchemaJson
  | Readonly<Record<string, CollectionOptions | CollectionSchema | CollectionBuilder>>
  | readonly (CollectionSchema | CollectionBuilder)[];

/** A validated schema with deterministic JSON serialization helpers. */
export class SchemaBuilder {
  private entries: (CollectionSchema | CollectionBuilder)[];

  public constructor(input: SchemaInput = []) {
    if (Array.isArray(input)) this.entries = [...input];
    else if ("collections" in input && Array.isArray(input.collections)) this.entries = [...input.collections];
    else this.entries = Object.entries(input).map(([name, options]) => new CollectionBuilder(name, options as CollectionOptions));
  }

  public addCollection(value: CollectionSchema | CollectionBuilder): SchemaBuilder {
    this.entries.push(value);
    return this;
  }

  public add(value: CollectionSchema | CollectionBuilder): SchemaBuilder {
    return this.addCollection(value);
  }

  public collection(name: string, options: CollectionOptions = {}): SchemaBuilder {
    return this.addCollection(new CollectionBuilder(name, options));
  }

  public build(): SchemaJson {
    const collections = this.entries.map((entry) => entry instanceof CollectionBuilder ? entry.build() : normalizeCollection(entry.name, entry));
    validateSchema({ collections });
    return { collections: sortByName(collections) };
  }

  public validate(): void {
    validateSchema(this.build());
  }

  public toJSON(): string {
    return serializeSchema(this.build());
  }

  public toJson(): string {
    return this.toJSON();
  }

  public toObject(): SchemaJson {
    return this.build();
  }
}

export const Schema = SchemaBuilder;
export type Schema = SchemaBuilder;
export function schemaBuilder(input: SchemaInput = []): SchemaBuilder {
  return new SchemaBuilder(input);
}
export const createSchemaBuilder = schemaBuilder;
export const newSchemaBuilder = schemaBuilder;
export function defineSchema(input: SchemaInput): SchemaBuilder {
  return new SchemaBuilder(input);
}
export const buildSchema = defineSchema;

export function serializeSchema(schema: SchemaJson | SchemaBuilder): string {
  const object = schema instanceof SchemaBuilder ? schema.build() : normalizeSchema(schema);
  validateSchema(object);
  return JSON.stringify(object);
}
export const schemaToJson = serializeSchema;

export function normalizeSchema(input: SchemaJson): SchemaJson {
  if (!input || !Array.isArray(input.collections)) throw new SchemaValidationError("schema must contain a collections array");
  const collections = input.collections.map((entry) => normalizeCollection(entry.name, entry));
  const version = input.version;
  assertSchemaVersion(version);
  return { collections: sortByName(collections), ...(version === undefined ? {} : { version }) };
}

export function validateSchema(schema: SchemaJson): void {
  if (!schema || !Array.isArray(schema.collections)) throw new SchemaValidationError("schema must contain a collections array");
  assertSchemaVersion(schema.version);
  const seen = new Set<string>();
  for (const collectionSchema of schema.collections) {
    const normalized = normalizeCollection(collectionSchema.name, collectionSchema);
    if (seen.has(normalized.name)) throw new SchemaValidationError(`duplicate collection ${JSON.stringify(normalized.name)}`);
    seen.add(normalized.name);
    validateCollection(normalized);
  }
}

export function validateCollection(collectionSchema: CollectionSchema): void {
  assertIdentifier(collectionSchema.name, "collection");
  assertSchemaVersion(collectionSchema.version);
  if (!Number.isInteger(collectionSchema.historyRetentionDays) || collectionSchema.historyRetentionDays < MIN_HISTORY_DAYS || collectionSchema.historyRetentionDays > MAX_HISTORY_DAYS) {
    throw new SchemaValidationError(`history retention for ${JSON.stringify(collectionSchema.name)} must be between 1 and 30 days`);
  }
  if (!["remove-wins", "hlc-tiebreak", "last-write-wins"].includes(collectionSchema.mergePolicy)) {
    throw new SchemaValidationError(`unknown merge policy ${JSON.stringify(collectionSchema.mergePolicy)}`);
  }
  const fields = new Map<string, FieldDef>();
  for (const fieldDefinition of collectionSchema.fields) {
    assertIdentifier(fieldDefinition.name, "field");
    if (fields.has(fieldDefinition.name)) throw new SchemaValidationError(`duplicate field ${JSON.stringify(fieldDefinition.name)} in ${JSON.stringify(collectionSchema.name)}`);
    assertFieldType(fieldDefinition.type);
    if (fieldDefinition.mergePolicy !== undefined && !CRDT_TYPE_SET.has(fieldDefinition.mergePolicy as CrdtFieldType)) throw new SchemaValidationError(`field ${JSON.stringify(fieldDefinition.name)} has a non-CRDT merge policy`);
    fields.set(fieldDefinition.name, fieldDefinition);
  }
  const indexes = collectionSchema.indexes ?? [];
  const indexNames = new Set<string>();
  for (const index of indexes) {
    assertIdentifier(index.name, "index");
    if (indexNames.has(index.name)) throw new SchemaValidationError(`duplicate index ${JSON.stringify(index.name)} in ${JSON.stringify(collectionSchema.name)}`);
    indexNames.add(index.name);
    if (index.fields.length === 0) throw new SchemaValidationError(`index ${JSON.stringify(index.name)} must contain a field`);
    if (index.kind !== undefined && index.kind !== "btree" && index.kind !== "fts") throw new SchemaValidationError(`unknown index kind ${JSON.stringify(index.kind)}`);
    if (index.kind === "fts" && index.fields.length !== 1) throw new SchemaValidationError(`FTS index ${JSON.stringify(index.name)} must contain exactly one field`);
    for (const name of index.fields) {
      assertIdentifier(name, "index field");
      if (!fields.has(name)) throw new SchemaValidationError(`index ${JSON.stringify(index.name)} references unknown field ${JSON.stringify(name)}`);
      if (index.kind === "fts") {
        const type = fields.get(name)?.type;
        if (type !== "text" && type !== "string") throw new SchemaValidationError(`FTS field ${JSON.stringify(name)} must have type text or string`);
      }
    }
  }
  const renames = collectionSchema.renames ?? [];
  const renameSources = new Set<string>();
  const renameTargets = new Set<string>();
  for (const rename of renames) {
    if (!rename || typeof rename.from !== "string" || typeof rename.to !== "string") throw new SchemaValidationError(`rename in ${JSON.stringify(collectionSchema.name)} must contain from and to names`);
    assertIdentifier(rename.from, "rename source");
    assertIdentifier(rename.to, "rename target");
    if (rename.from === rename.to) throw new SchemaValidationError(`rename ${JSON.stringify(rename.from)} -> ${JSON.stringify(rename.to)} must change the field name`);
    if (renameSources.has(rename.from)) throw new SchemaValidationError(`duplicate rename source ${JSON.stringify(rename.from)}`);
    if (renameTargets.has(rename.to)) throw new SchemaValidationError(`duplicate rename target ${JSON.stringify(rename.to)}`);
    if (fields.has(rename.from)) throw new SchemaValidationError(`rename source ${JSON.stringify(rename.from)} is still declared as a field`);
    if (!fields.has(rename.to)) throw new SchemaValidationError(`rename target ${JSON.stringify(rename.to)} is not declared as a field`);
    renameSources.add(rename.from);
    renameTargets.add(rename.to);
  }
  if (collectionSchema.fts) {
    if (collectionSchema.fts.fields.length === 0) throw new SchemaValidationError(`FTS in ${JSON.stringify(collectionSchema.name)} must contain a field`);
    for (const name of collectionSchema.fts.fields) {
      assertIdentifier(name, "FTS field");
      const definition = fields.get(name);
      if (!definition) throw new SchemaValidationError(`FTS references unknown field ${JSON.stringify(name)}`);
      if (definition.type !== "text" && definition.type !== "string") throw new SchemaValidationError(`FTS field ${JSON.stringify(name)} must have type text or string`);
    }
  }
  if (collectionSchema.ttl) {
    assertIdentifier(collectionSchema.ttl.field, "TTL field");
    const definition = fields.get(collectionSchema.ttl.field);
    if (!definition) throw new SchemaValidationError(`TTL references unknown field ${JSON.stringify(collectionSchema.ttl.field)}`);
    if (definition.type !== "timestamp") throw new SchemaValidationError(`TTL field ${JSON.stringify(collectionSchema.ttl.field)} must have type timestamp`);
  }
}

export class SchemaValidationError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "SchemaValidationError";
  }
}

function normalizeCollection(name: string, input: CollectionOptions | CollectionSchema): CollectionSchema {
  const fieldsInput = input.fields ?? [];
  const fields = Array.isArray(fieldsInput)
    ? fieldsInput.map((value) => value instanceof FieldBuilder ? value.toDefinition() : normalizeField(undefined, value))
    : Object.entries(fieldsInput).map(([fieldName, value]) => normalizeField(fieldName, value));
  const indexesInput = input.indexes ?? [];
  const indexes = Array.isArray(indexesInput)
    ? indexesInput.map((value) => normalizeIndex(value))
    : Object.entries(indexesInput).map(([indexName, value]) => normalizeIndex(
      Array.isArray(value)
        ? { name: indexName, fields: value }
        : { name: indexName, fields: (value as IndexOptions).fields, ...((value as IndexOptions).unique === true ? { unique: true } : {}) },
    ));
  const ftsInput = input.fts;
  const fts = {
    fields: [...(ftsInput === undefined ? [] : typeof ftsInput === "string" ? [ftsInput] : Array.isArray(ftsInput) ? ftsInput : (ftsInput as FtsDef).fields)],
  };
  const markedFts = fields.filter((field) => field.fts === true).map((field) => field.name);
  const ftsIndexes = [...fts.fields, ...markedFts].filter((field, position, all) => all.indexOf(field) === position).map((field) => normalizeIndex({ name: `${field}_fts`, fields: [field], kind: "fts" }));
  const allIndexes = [...indexes, ...ftsIndexes];
  const canonicalFields = fields.map(({ fts: _fts, ...field }) => ({ ...field, type: canonicalFieldType(field.type), ...(field.mergePolicy === undefined ? {} : { mergePolicy: canonicalFieldType(field.mergePolicy) }) }));
  const options = input as CollectionOptions;
  const retention = input.historyRetentionDays ?? options.historyWindowDays ?? options.history ?? DEFAULT_HISTORY_DAYS;
  const ttl = typeof input.ttl === "string" ? { field: input.ttl } : input.ttl;
  const renamesInput = input.renames;
  const renames = renamesInput === undefined
    ? []
    : Array.isArray(renamesInput)
      ? renamesInput.map((rename) => ({ from: rename.from.trim(), to: rename.to.trim() }))
      : Object.entries(renamesInput).map(([from, to]) => ({ from: from.trim(), to: to.trim() }));
  const version: number | undefined = input.version ?? options.schemaVersion;
  assertSchemaVersion(version);
  return {
    name: name.trim(),
    fields: canonicalFields,
    ...(allIndexes.length > 0 ? { indexes: allIndexes } : {}),
    ...(renames.length > 0 ? { renames: sortRenames(renames) } : {}),
    ...(ttl ? { ttl: { field: ttl.field.trim() } } : {}),
    ...(version === undefined ? {} : { version }),
    historyRetentionDays: retention,
    mergePolicy: input.mergePolicy ?? input.merge ?? "remove-wins",
  };
}

function normalizeField(name: string | undefined, input: FieldDefInput): FieldDef {
  const value: FieldDef | (Exclude<FieldDefInput, FieldDef | FieldBuilder | FieldType>) = input instanceof FieldBuilder ? input.toDefinition(name) : typeof input === "string" ? { name: name ?? "", type: input, required: true, indexed: false } : input;
  const fieldName = name ?? ("name" in value ? value.name : "");
  if ("required" in value && "optional" in value && value.required !== undefined && value.optional !== undefined) throw new SchemaValidationError(`field ${JSON.stringify(fieldName)} cannot contain both required and optional`);
  const required = "required" in value ? value.required ?? !(value.optional === true) : !("optional" in value && value.optional === true);
  const unique = "unique" in value && value.unique === true;
  const indexed = unique || ("indexed" in value && value.indexed === true);
  const mergePolicy = "mergePolicy" in value ? value.mergePolicy : "merge" in value ? value.merge : undefined;
  const fts = "fts" in value && value.fts === true;
  return { name: fieldName.trim(), type: canonicalFieldType(value.type), required, indexed, ...(unique ? { unique: true } : {}), ...(mergePolicy ? { mergePolicy: canonicalFieldType(mergePolicy) } : {}), ...(fts ? { fts: true } : {}) };
}

function normalizeIndex(input: IndexInput): IndexDef {
  const fields = typeof input.fields === "string" ? [input.fields] : [...input.fields];
  return { name: input.name.trim(), fields: fields.map((value) => value.trim()), ...(input.unique === true ? { unique: true } : {}), kind: input.kind ?? "btree" };
}

function assertFieldType(type: string): asserts type is FieldType {
  if (!FIELD_TYPE_SET.has(type)) throw new SchemaValidationError(`unknown field type ${JSON.stringify(type)}`);
}

function canonicalFieldType(type: string): FieldType {
  if (type === "integer") return "int";
  if (type === "boolean") return "bool";
  if (type === "pn-counter") return "counter-pn";
  if (type === "add-wins-set") return "set-add-wins";
  if (type === "remove-wins-set") return "set-remove-wins";
  if (type === "mv-register") return "register-mv";
  assertFieldType(type);
  return type;
}

function assertSchemaVersion(version: unknown): asserts version is number | undefined {
  if (version !== undefined && (typeof version !== "number" || !Number.isSafeInteger(version) || version < 1)) {
    throw new SchemaValidationError("schema version must be a positive safe integer");
  }
}

function sortByName<T extends { readonly name: string }>(values: readonly T[]): T[] {
  return [...values].sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
}

function sortRenames(values: readonly FieldRename[]): FieldRename[] {
  return [...values].sort((left, right) => left.from < right.from ? -1 : left.from > right.from ? 1 : left.to < right.to ? -1 : left.to > right.to ? 1 : 0);
}

function cloneCollectionOptions(options: CollectionOptions): CollectionOptions {
  return {
    ...options,
    ...(options.fields === undefined ? {} : { fields: Array.isArray(options.fields) ? [...options.fields] : { ...options.fields } }),
    ...(options.indexes === undefined ? {} : { indexes: Array.isArray(options.indexes) ? [...options.indexes] : { ...options.indexes } }),
    ...(options.renames === undefined ? {} : { renames: Array.isArray(options.renames) ? options.renames.map((rename) => ({ ...rename })) : { ...options.renames } }),
  };
}

function assertIdentifier(value: string, kind: string): void {
  if (typeof value !== "string" || value.length === 0 || value.length > 255 || !IDENTIFIER.test(value)) throw new SchemaValidationError(`${kind} name ${JSON.stringify(value)} must match [A-Za-z_][A-Za-z0-9_]*`);
}
