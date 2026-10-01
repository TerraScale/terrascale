/**
 * Pure parameterized SQL construction. Listener capabilities are checked by the PostgreSQL transport.
 * This module performs no execution and grants no server SQL capabilities.
 *
 * The typed builder accepts schema field descriptors, not SQL fragments. All
 * values (including LIMIT/OFFSET values) are kept separately from the SQL
 * statement and are sent as positional parameters.
 */

import { FieldTypes } from "./schema.js";
/** @import { CollectionSchema, FieldDef, FieldType } from "./schema.js" */

/**
 * @typedef {Pick<FieldDef, "name" | "type"> &
 *   Partial<Omit<FieldDef, "name" | "type">>} SqlFieldDef
 */
/**
 * @typedef {{
 *   readonly name: string;
 *   readonly fields: readonly SqlFieldDef[];
 * } & Partial<Omit<CollectionSchema, "name" | "fields">>} SqlCollectionSchema
 */
/**
 * @template {SqlCollectionSchema} S
 * @typedef {S["fields"][number]} SchemaField
 */
/**
 * @template {SqlCollectionSchema} S
 * @typedef {SchemaField<S>} QueryField
 */

export const Direction = Object.freeze(
  /** @type {const} */ ({
    Asc: "ASC",
    Desc: "DESC",
  }),
);

/** @typedef {(typeof Direction)[keyof typeof Direction]} Direction */
/** @typedef {boolean | bigint | number | string | Uint8Array | null} SqlScalar */
/** @typedef {SqlScalar} SqlParameter */

export class SqlValidationError extends Error {
  /**
   * @override
   * @readonly
   */
  name = /** @type {const} */ ("SqlValidationError");
  /** @readonly */
  code = /** @type {const} */ ("invalid_sql");

  /**
   * @param {string} message
   */
  constructor(message) {
    super(`terrascale SQL: ${message}`);
  }
}

/**
 * @typedef {{
 *   readonly statement: string;
 *   readonly parameters: readonly SqlParameter[];
 * }} QueryDescriptor
 */

/**
 * @param {readonly SqlParameter[]} parameters
 * @returns {readonly SqlParameter[]}
 */
function freezeParameters(parameters) {
  return Object.freeze(
    parameters.map((parameter) => cloneParameter(parameter)),
  );
}

/**
 * @param {SqlParameter} parameter
 * @returns {SqlParameter}
 */
function cloneParameter(parameter) {
  return parameter instanceof Uint8Array ? parameter.slice() : parameter;
}

/** A validated, parameterized read-only SQL statement. */
export class Query {
  /**
   * @readonly
   * @type {string}
   */
  statement;
  /** @type {readonly SqlParameter[]} */
  #parameters;
  /**
   * @returns {readonly SqlParameter[]}
   */
  get parameters() {
    return freezeParameters(this.#parameters);
  }

  /**
   * @private
   * @param {string} sql
   * @param {readonly SqlParameter[]} parameters
   */
  constructor(sql, parameters) {
    validateSql(sql, parameters);
    this.statement = sql;
    this.#parameters = freezeParameters(parameters);
    Object.freeze(this);
  }

  /**
   * @param {string} sql
   * @param {readonly SqlParameter[]} [parameters]
   * @returns {Query}
   */
  static raw(sql, parameters = []) {
    return new Query(sql, parameters);
  }

  /**
   * @param {readonly string[]} [columns]
   * @returns {SelectBuilder}
   */
  static select(columns = ["*"]) {
    return SelectBuilder.fromColumns(columns);
  }

  /**
   * @template {SqlCollectionSchema} S
   * @param {S} schema
   * @returns {SelectBuilder<S>}
   */
  static selectSchema(schema) {
    return SelectBuilder.fromSchema(schema);
  }

  /**
   * @returns {QueryDescriptor}
   */
  toDescriptor() {
    return Object.freeze({
      statement: this.statement,
      parameters: this.parameters,
    });
  }
}

/**
 * Schema-bound SELECT builder.
 *
 * @template {SqlCollectionSchema} [S=SqlCollectionSchema]
 */
export class SelectBuilder {
  /** @type {readonly string[]} */
  #columns;
  /** @type {string | undefined} */
  #from;
  /** @type {S | undefined} */
  #schema;
  /** @type {readonly string[]} */
  #predicates;
  /** @type {readonly (readonly [string, Direction])[]} */
  #order;
  /** @type {number | undefined} */
  #limit;
  /** @type {number | undefined} */
  #offset;
  /** @type {readonly SqlParameter[]} */
  #parameters;
  /** @type {boolean} */
  #schemaBound;

  /**
   * @private
   * @param {{
   *   columns: readonly string[];
   *   from?: string | undefined;
   *   schema?: S | undefined;
   *   predicates?: readonly string[];
   *   order?: readonly (readonly [string, Direction])[];
   *   limit?: number | undefined;
   *   offset?: number | undefined;
   *   parameters?: readonly SqlParameter[];
   *   schemaBound?: boolean;
   * }} input
   */
  constructor(input) {
    this.#columns = Object.freeze([...input.columns]);
    this.#from = input.from;
    this.#schema = input.schema;
    this.#predicates = Object.freeze([...(input.predicates ?? [])]);
    this.#order = input.order ?? [];
    this.#limit = input.limit;
    this.#offset = input.offset;
    this.#parameters = freezeParameters(input.parameters ?? []);
    this.#schemaBound = input.schemaBound ?? false;
  }

  /**
   * @param {Partial<{
   *   columns: readonly string[];
   *   from: string | undefined;
   *   predicates: readonly string[];
   *   order: readonly (readonly [string, Direction])[];
   *   limit: number | undefined;
   *   offset: number | undefined;
   *   parameters: readonly SqlParameter[];
   * }>} changes
   * @returns {SelectBuilder<S>}
   */
  #copy(changes) {
    return /** @type {SelectBuilder<S>} */ (
      new SelectBuilder({
        columns: changes.columns ?? this.#columns,
        from: changes.from === undefined ? this.#from : changes.from,
        schema: this.#schema,
        predicates: changes.predicates ?? this.#predicates,
        order: changes.order ?? this.#order,
        limit: changes.limit === undefined ? this.#limit : changes.limit,
        offset: changes.offset === undefined ? this.#offset : changes.offset,
        parameters: changes.parameters ?? this.#parameters,
        schemaBound: this.#schemaBound,
      })
    );
  }

  /**
   * @param {readonly string[]} columns
   * @returns {SelectBuilder}
   */
  static fromColumns(columns) {
    return new SelectBuilder({ columns: [...columns] });
  }

  /**
   * @template {SqlCollectionSchema} T
   * @param {T} schema
   * @returns {SelectBuilder<T>}
   */
  static fromSchema(schema) {
    validateIdentifier(schema.name, "collection");
    if (!Array.isArray(schema.fields))
      throw new SqlValidationError("schema fields must be an array");
    /** @type {Set<string>} */
    const names = new Set();
    for (const field of schema.fields) {
      validateField(field, "schema field");
      if (names.has(field.name))
        throw new SqlValidationError(`duplicate schema field '${field.name}'`);
      names.add(field.name);
    }
    return /** @type {SelectBuilder<T>} */ (
      new SelectBuilder({
        columns: ["*"],
        from: schema.name,
        schema: /** @type {T} */ (
          Object.freeze({
            ...schema,
            fields: Object.freeze(
              schema.fields.map((field) => Object.freeze({ ...field })),
            ),
          })
        ),
        schemaBound: true,
      })
    );
  }

  /**
   * @param {QueryField<S>} field
   * @returns {SelectBuilder<S>}
   */
  column(field) {
    const name = this.#checkedField(field, "column");
    return this.#copy({
      columns: [
        ...(this.#columns.length === 1 && this.#columns[0] === "*"
          ? []
          : this.#columns),
        name,
      ],
    });
  }

  /**
   * @param {readonly QueryField<S>[]} fields
   * @returns {SelectBuilder<S>}
   */
  columns(fields) {
    /** @type {SelectBuilder<S>} */
    let builder = this;
    for (const field of fields) builder = builder.column(field);
    return builder;
  }

  /**
   * @param {string} collection
   * @returns {SelectBuilder<S>}
   */
  from(collection) {
    const valid = validateIdentifier(collection, "collection");
    if (this.#schemaBound && valid !== this.#from) {
      throw new SqlValidationError(
        "a schema-bound query cannot change its collection",
      );
    }
    return this.#copy({ from: valid });
  }

  /**
   * @template {SqlParameter} T
   * @param {QueryField<S>} field
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  whereEq(field, value) {
    return this.#comparison(field, "=", value);
  }

  /**
   * @template {SqlParameter} T
   * @param {QueryField<S>} field
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  whereNe(field, value) {
    return this.#comparison(field, "<>", value);
  }

  /**
   * @template {SqlParameter} T
   * @param {QueryField<S>} field
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  whereGt(field, value) {
    return this.#comparison(field, ">", value);
  }

  /**
   * @template {SqlParameter} T
   * @param {QueryField<S>} field
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  whereGte(field, value) {
    return this.#comparison(field, ">=", value);
  }

  /**
   * @template {SqlParameter} T
   * @param {QueryField<S>} field
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  whereLt(field, value) {
    return this.#comparison(field, "<", value);
  }

  /**
   * @template {SqlParameter} T
   * @param {QueryField<S>} field
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  whereLte(field, value) {
    return this.#comparison(field, "<=", value);
  }

  /**
   * @template {SqlParameter} T
   * @param {QueryField<S>} field
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  whereLike(field, value) {
    return this.#comparison(field, "LIKE", value);
  }

  /**
   * @template {SqlParameter} T
   * @param {QueryField<S>} field
   * @param {readonly T[]} values
   * @returns {SelectBuilder<S>}
   */
  whereIn(field, values) {
    if (values.length === 0)
      throw new SqlValidationError("IN requires at least one value");
    const name = this.#checkedField(field, "predicate");
    const placeholders = Array.from(
      { length: values.length },
      (_, index) => `$${this.#parameters.length + index + 1}`,
    );
    return this.#copy({
      predicates: [
        ...this.#predicates,
        `${name} IN (${placeholders.join(", ")})`,
      ],
      parameters: [...this.#parameters, ...values],
    });
  }

  /**
   * @param {SchemaField<S>} field
   * @param {Direction} [direction]
   * @returns {SelectBuilder<S>}
   */
  orderByField(field, direction = Direction.Asc) {
    return this.orderBy(this.#checkedField(field, "order_by"), direction);
  }

  /**
   * @param {string} column
   * @param {Direction} [direction]
   * @returns {SelectBuilder<S>}
   */
  orderBy(column, direction = Direction.Asc) {
    if (direction !== Direction.Asc && direction !== Direction.Desc) {
      throw new SqlValidationError("ORDER BY direction must be ASC or DESC");
    }
    const valid = this.#schemaBound
      ? this.#checkedFieldName(column, "order_by")
      : validateIdentifier(column, "order_by");
    return this.#copy({ order: [...this.#order, [valid, direction]] });
  }

  /**
   * @param {number} value
   * @returns {SelectBuilder<S>}
   */
  limit(value) {
    return this.#paginationValue(value, "limit", { limit: value });
  }

  /**
   * @param {number} value
   * @returns {SelectBuilder<S>}
   */
  offset(value) {
    return this.#paginationValue(value, "offset", { offset: value });
  }

  /**
   * @param {{
   *   readonly limit: number;
   *   readonly offset?: number | undefined;
   * }} options
   * @returns {SelectBuilder<S>}
   */
  paginate(options) {
    const withLimit = this.limit(options.limit);
    return options.offset === undefined
      ? withLimit
      : withLimit.offset(options.offset);
  }

  /**
   * @param {number} page
   * @param {number} pageSize
   * @returns {SelectBuilder<S>}
   */
  page(page, pageSize) {
    if (!Number.isSafeInteger(page) || page < 1)
      throw new SqlValidationError("page must be a positive integer");
    const result = this.limit(pageSize);
    return result.offset((page - 1) * pageSize);
  }

  /**
   * @returns {Query}
   */
  build() {
    if (this.#from === undefined)
      throw new SqlValidationError("a query requires .from(collection)");
    if (this.#columns.length === 0)
      throw new SqlValidationError(
        "a query requires at least one selected column",
      );
    const columns = this.#columns.map(validateProjection);
    const parts = [
      `SELECT ${columns.join(", ")} FROM ${validateIdentifier(this.#from, "collection")}`,
    ];
    if (this.#predicates.length > 0)
      parts.push(`WHERE ${this.#predicates.join(" AND ")}`);
    if (this.#order.length > 0) {
      parts.push(
        `ORDER BY ${this.#order.map(([column, direction]) => `${column} ${direction}`).join(", ")}`,
      );
    }
    const parameters = [...this.#parameters];
    if (this.#limit !== undefined) {
      parts.push(`LIMIT $${parameters.length + 1}`);
      parameters.push(this.#limit);
    }
    if (this.#offset !== undefined) {
      parts.push(`OFFSET $${parameters.length + 1}`);
      parameters.push(this.#offset);
    }
    return Query.raw(parts.join(" "), parameters);
  }

  /**
   * @returns {QueryDescriptor}
   */
  toDescriptor() {
    return this.build().toDescriptor();
  }

  /**
   * @template {SqlParameter} T
   * @param {SchemaField<S>} field
   * @param {"=" | "<>" | "!=" | ">" | ">=" | "<" | "<=" | "MATCH" | "LIKE"} operator
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  #comparison(field, operator, value) {
    return this.#comparisonExpression(
      this.#checkedField(field, "predicate"),
      operator,
      value,
    );
  }

  /**
   * @template {SqlParameter} T
   * @param {string} expression
   * @param {"=" | "<>" | "!=" | ">" | ">=" | "<" | "<=" | "MATCH" | "LIKE"} operator
   * @param {T} value
   * @returns {SelectBuilder<S>}
   */
  #comparisonExpression(expression, operator, value) {
    if (!COMPARISON_OPERATORS.has(operator))
      throw new SqlValidationError("unsupported SQL comparison operator");
    const placeholder = `$${this.#parameters.length + 1}`;
    return this.#copy({
      predicates: [
        ...this.#predicates,
        `${expression} ${operator} ${placeholder}`,
      ],
      parameters: [...this.#parameters, value],
    });
  }

  /**
   * @param {number} value
   * @param {string} label
   * @param {{ readonly limit?: number; readonly offset?: number }} pagination
   * @returns {SelectBuilder<S>}
   */
  #paginationValue(value, label, pagination) {
    if (!Number.isSafeInteger(value) || value < 0)
      throw new SqlValidationError(
        `${label} must be a non-negative safe integer`,
      );
    return this.#copy({
      limit: pagination.limit === undefined ? this.#limit : pagination.limit,
      offset:
        pagination.offset === undefined ? this.#offset : pagination.offset,
    });
  }

  /**
   * @param {QueryField<S>} field
   * @param {string} context
   * @returns {string}
   */
  #checkedField(field, context) {
    return this.#checkedFieldDef(field, context).name;
  }

  /**
   * @param {QueryField<S>} field
   * @param {string} context
   * @returns {SqlFieldDef}
   */
  #checkedFieldDef(field, context) {
    if (!field || typeof field !== "object")
      throw new SqlValidationError(`${context} field is invalid`);
    const name = validateIdentifier(field.name, context);
    if (!this.#schemaBound || this.#schema === undefined)
      throw new SqlValidationError(
        "use a schema-bound SELECT before passing field descriptors",
      );
    const declared = this.#schema.fields.find(
      (candidate) => candidate.name === name,
    );
    if (declared === undefined || fieldType(declared) !== fieldType(field)) {
      throw new SqlValidationError(
        `${context} field '${name}' is not declared by this collection`,
      );
    }
    return declared;
  }

  /**
   * @param {string} name
   * @param {string} context
   * @returns {string}
   */
  #checkedFieldName(name, context) {
    validateIdentifier(name, context);
    if (
      !this.#schemaBound ||
      this.#schema === undefined ||
      !this.#schema.fields.some((field) => field.name === name)
    ) {
      throw new SqlValidationError(
        `${context} field '${name}' is not declared by this collection`,
      );
    }
    return name;
  }
}

/**
 * @template {SqlCollectionSchema} S
 * @overload
 * @param {S} schema
 * @returns {SelectBuilder<S>}
 */
/**
 * @overload
 * @param {readonly string[]} [columns]
 * @returns {SelectBuilder}
 */
/**
 * @param {SqlCollectionSchema | readonly string[]} [input]
 * @returns {SelectBuilder}
 */
export function select(input = ["*"]) {
  if (Array.isArray(input)) return SelectBuilder.fromColumns(input);
  return SelectBuilder.fromSchema(/** @type {CollectionSchema} */ (input));
}

/**
 * @template {SqlCollectionSchema} S
 * @param {S} schema
 * @returns {SelectBuilder<S>}
 */
export function selectSchema(schema) {
  return SelectBuilder.fromSchema(schema);
}

/**
 * @param {string} sql
 * @param {readonly SqlParameter[]} [parameters]
 * @returns {Query}
 */
export function raw(sql, parameters = []) {
  return Query.raw(sql, parameters);
}

/** @type {Set<string>} */
const COMPARISON_OPERATORS = new Set([
  "=",
  "<>",
  "!=",
  ">",
  ">=",
  "<",
  "<=",
  "MATCH",
  "LIKE",
]);

/**
 * @param {SqlFieldDef} field
 * @returns {FieldType | undefined}
 */
function fieldType(field) {
  return normalizeFieldType(field.type);
}

/**
 * @param {FieldType | string} value
 * @returns {FieldType | undefined}
 */
function normalizeFieldType(value) {
  return Object.values(FieldTypes).includes(/** @type {FieldType} */ (value))
    ? /** @type {FieldType} */ (value)
    : undefined;
}

/**
 * @param {SqlFieldDef} field
 * @param {string} context
 * @returns {void}
 */
function validateField(field, context) {
  validateIdentifier(field.name, context);
  if (fieldType(field) === undefined)
    throw new SqlValidationError(
      `${context} '${field.name}' has an unknown type`,
    );
}

/**
 * @param {string} value
 * @returns {boolean}
 */
function isIdentifier(value) {
  return /^[A-Za-z_][A-Za-z0-9_]*$/u.test(value);
}

/**
 * @param {string} value
 * @param {string} context
 * @returns {string}
 */
function validateIdentifier(value, context) {
  if (!isIdentifier(value))
    throw new SqlValidationError(
      `${context} must match [A-Za-z_][A-Za-z0-9_]*`,
    );
  return value;
}

/**
 * @param {string} value
 * @returns {string}
 */
function validateProjection(value) {
  if (value.trim() === "*") return "*";
  return validateIdentifier(value, "column");
}

const FORBIDDEN_KEYWORDS = new Set([
  "attach",
  "alter",
  "analyze",
  "begin",
  "commit",
  "create",
  "delete",
  "detach",
  "drop",
  "end",
  "insert",
  "pragma",
  "reindex",
  "replace",
  "rollback",
  "savepoint",
  "transaction",
  "truncate",
  "update",
  "vacuum",
  "upsert",
]);
const FORBIDDEN_FUNCTIONS = new Set([
  "random",
  "randomblob",
  "changes",
  "total_changes",
  "last_insert_rowid",
  "load_extension",
  "readfile",
  "writefile",
  "datetime",
  "date",
  "time",
  "strftime",
  "julianday",
  "unixepoch",
]);

/**
 * Validate the client-side read-only SQL allow-list and positional bindings.
 *
 * @param {string} sql
 * @param {readonly SqlParameter[]} [parameters]
 * @returns {void}
 */
export function validateSql(sql, parameters = []) {
  if (typeof sql !== "string" || sql.trim() === "")
    throw new SqlValidationError("SQL must not be empty");
  if (new TextEncoder().encode(sql).length > 1_048_576)
    throw new SqlValidationError("SQL exceeds the 1 MiB query text limit");
  for (const parameter of parameters) validateParameter(parameter);
  const tokens = tokenizeSql(sql);
  const first = tokens[0]?.toLowerCase();
  let bodyStart = 1;
  if (first === "select") {
    // normal SELECT
  } else if (first === "with") {
    if (tokens[1]?.toLowerCase() === "recursive")
      throw new SqlValidationError("WITH RECURSIVE is not allowed");
  } else if (first === "explain") {
    if (tokens[1]?.toLowerCase() === "query") {
      if (tokens[2]?.toLowerCase() !== "plan")
        throw new SqlValidationError(
          "EXPLAIN may only use EXPLAIN QUERY PLAN or EXPLAIN SELECT",
        );
      bodyStart = 3;
    }
    if (
      tokens[bodyStart]?.toLowerCase() !== "select" &&
      tokens[bodyStart]?.toLowerCase() !== "with"
    ) {
      throw new SqlValidationError(
        "only EXPLAIN SELECT and EXPLAIN QUERY PLAN SELECT are allowed",
      );
    }
    if (
      tokens[bodyStart]?.toLowerCase() === "with" &&
      tokens[bodyStart + 1]?.toLowerCase() === "recursive"
    ) {
      throw new SqlValidationError("WITH RECURSIVE is not allowed");
    }
  } else {
    throw new SqlValidationError(
      "only SELECT, WITH, and EXPLAIN statements are allowed",
    );
  }
  for (let index = bodyStart; index < tokens.length; index += 1) {
    const token = /** @type {string} */ (tokens[index]).toLowerCase();
    if (FORBIDDEN_KEYWORDS.has(token))
      throw new SqlValidationError(
        "SQL contains a DDL, DML, or transaction keyword",
      );
    if (["current_date", "current_time", "current_timestamp"].includes(token))
      throw new SqlValidationError(
        "non-deterministic SQL functions are not allowed",
      );
    if (FORBIDDEN_FUNCTIONS.has(token) && tokens[index + 1] === "(")
      throw new SqlValidationError("unsafe SQL functions are not allowed");
    if (token.startsWith("sqlite_") || token.startsWith("pragma_"))
      throw new SqlValidationError(
        "internal SQLite names and pragma functions are not allowed",
      );
  }
  validateBindings(tokens, parameters.length);
}

export const validate_sql = validateSql;

/**
 * @param {unknown} value
 * @returns {void}
 */
function validateParameter(value) {
  if (
    value === null ||
    typeof value === "string" ||
    typeof value === "boolean" ||
    value instanceof Uint8Array
  )
    return;
  if (typeof value === "bigint" && value >= -(1n << 63n) && value < 1n << 63n)
    return;
  if (typeof value === "number" && Number.isSafeInteger(value)) return;
  throw new SqlValidationError(
    "SQL parameters require exact i64 integers, strings, booleans, bytes or null; listener support is separate.",
  );
}

/**
 * @param {string} sql
 * @returns {string[]}
 */
function tokenizeSql(sql) {
  /** @type {string[]} */
  const tokens = [];
  const bytes = new TextEncoder().encode(sql);
  let index = 0;
  while (index < bytes.length) {
    const byte = /** @type {number} */ (bytes[index]);
    if (byte === 32 || byte === 9 || byte === 10 || byte === 13) {
      index += 1;
      continue;
    }
    if (byte === 59) {
      if (
        bytes
          .slice(index + 1)
          .some(
            (next) => next !== 32 && next !== 9 && next !== 10 && next !== 13,
          )
      )
        throw new SqlValidationError("multiple SQL statements are not allowed");
      index += 1;
      continue;
    }
    if (
      (byte === 45 && bytes[index + 1] === 45) ||
      (byte === 47 && bytes[index + 1] === 42)
    )
      throw new SqlValidationError("SQL comments are not allowed");
    if (byte === 39 || byte === 34 || byte === 96 || byte === 91) {
      const end = byte === 91 ? 93 : byte;
      index += 1;
      let closed = false;
      while (index < bytes.length) {
        if (bytes[index] === end) {
          if (end !== 93 && bytes[index + 1] === end) {
            index += 2;
            continue;
          }
          index += 1;
          closed = true;
          break;
        }
        index += 1;
      }
      if (!closed)
        throw new SqlValidationError(
          "SQL contains an unterminated quoted value",
        );
      tokens.push("<quoted>");
      continue;
    }
    if (byte === 36) {
      const start = index;
      index += 1;
      while (
        index < bytes.length &&
        /** @type {number} */ (bytes[index]) >= 48 &&
        /** @type {number} */ (bytes[index]) <= 57
      )
        index += 1;
      if (index === start + 1)
        throw new SqlValidationError("only numbered $n parameters are allowed");
      tokens.push(new TextDecoder().decode(bytes.slice(start, index)));
      continue;
    }
    if (byte === 63) {
      const start = index;
      index += 1;
      while (
        index < bytes.length &&
        /** @type {number} */ (bytes[index]) >= 48 &&
        /** @type {number} */ (bytes[index]) <= 57
      )
        index += 1;
      tokens.push(new TextDecoder().decode(bytes.slice(start, index)));
      continue;
    }
    if (
      (byte >= 48 && byte <= 57) ||
      (byte >= 65 && byte <= 90) ||
      (byte >= 97 && byte <= 122) ||
      byte === 95
    ) {
      const start = index;
      index += 1;
      while (
        index < bytes.length &&
        ((/** @type {number} */ (bytes[index]) >= 48 &&
          /** @type {number} */ (bytes[index]) <= 57) ||
          (/** @type {number} */ (bytes[index]) >= 65 &&
            /** @type {number} */ (bytes[index]) <= 90) ||
          (/** @type {number} */ (bytes[index]) >= 97 &&
            /** @type {number} */ (bytes[index]) <= 122) ||
          bytes[index] === 95)
      )
        index += 1;
      tokens.push(new TextDecoder().decode(bytes.slice(start, index)));
      continue;
    }
    tokens.push(new TextDecoder().decode(bytes.slice(index, index + 1)));
    index += 1;
  }
  return tokens;
}

/**
 * @param {readonly string[]} tokens
 * @param {number} parameterCount
 * @returns {void}
 */
function validateBindings(tokens, parameterCount) {
  let nextUnnamed = 1;
  let highest = 0;
  /** @type {Set<number>} */
  const seen = new Set();
  for (const token of tokens) {
    /** @type {number | undefined} */
    let number;
    if (/^\$[0-9]+$/u.test(token)) number = Number(token.slice(1));
    else if (token === "?") {
      number = nextUnnamed;
      nextUnnamed += 1;
    } else if (/^\?[0-9]+$/u.test(token)) number = Number(token.slice(1));
    if (number === undefined) continue;
    if (!Number.isSafeInteger(number) || number < 1)
      throw new SqlValidationError("parameter numbering starts at 1");
    seen.add(number);
    highest = Math.max(highest, number);
  }
  if (highest !== parameterCount || seen.size !== parameterCount)
    throw new SqlValidationError(
      `SQL has ${highest} positional parameter slots but ${parameterCount} values were supplied`,
    );
}
