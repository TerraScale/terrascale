import { ExactDecimal } from "./v1/json.js";
import { SqlError } from "./v1/errors.js";
/** @import { V1Database } from "./v1/terrabase-v1.js" */
/** @import { V1SqlColumn, V1SqlExecuteResult } from "./v1/sql-execute.js" */
/** @import { V1SqlSocket } from "./v1/sql-socket.js" */
/** @import { V1SqlValue } from "./v1/sql-execute.js" */

/** @typedef {0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 64 | 65 | 66 | 67 | 68 | 69 | 70 | 71 | 72 | 73 | 74 | 75 | 76 | 77 | 78 | 128} PrismaColumnType */

/** @typedef {object} PrismaSqlQuery @property {string} sql @property {unknown[]} args @property {{ scalarType: string; arity: string }[] | undefined} argTypes */

/**
 * A raw result set shaped for a driver-adapter query engine: column types and names in matching order, rows of values and the last insert id.
 * @typedef {object} PrismaSqlResultSet
 * @property {PrismaColumnType[]} columnTypes
 * @property {string[]} columnNames
 * @property {unknown[][]} rows
 * @property {string} [lastInsertId]
 */

/** @typedef {{ readonly scalarType?: string | undefined }} PrismaArgType */

/** @typedef {"READ UNCOMMITTED" | "READ COMMITTED" | "REPEATABLE READ" | "SNAPSHOT" | "SERIALIZABLE"} PrismaIsolationLevel */

/**
 * @param {PrismaSqlQuery} query
 * @returns {import("./v1/sql-execute.js").V1SqlArgument[]}
 */
function sqlArguments(query) {
  return query.args.map((arg, index) => mapArgument(arg, query.argTypes?.[index]));
}

/**
 * @param {V1SqlExecuteResult} result
 * @returns {PrismaSqlResultSet}
 */
function sqlResultSet(result) {
  const types = columnTypes(result.columns, result.rows);
  /** @type {PrismaSqlResultSet} */
  const resultSet = {
    columnTypes: types,
    columnNames: result.columns.map(column => column.name),
    rows: result.rows.map(row => row.map((value, index) => mapValue(value, types[index] ?? columnTypeEnum.Int32))),
  };
  if (result.lastInsertRowid !== null) resultSet.lastInsertId = String(result.lastInsertRowid);
  return resultSet;
}

/**
 * @param {unknown} error
 * @returns {unknown}
 */
function transactionError(error) {
  if (error instanceof SqlError && error.code === "query_canceled" && error.message.includes("5 seconds"))
    return new Error(`Interactive transaction timed out: the server ends one transaction after 5 seconds (${error.message}).`);
  return error;
}

/**
 * @param {unknown} error
 * @returns {unknown}
 */
function adapterError(error) {
  if (!(error instanceof SqlError)) return error;
  const kind = mappedErrorKind(error.sqlstate);
  const mapped = { ...kind ?? { kind: "sqlite", extendedCode: 1, message: error.message }, originalCode: error.code, originalMessage: error.message };
  const wrapped = new Error(error.message, { cause: mapped });
  wrapped.name = "DriverAdapterError";
  return wrapped;
}

/** @param {string} sqlstate @returns {{ kind: string } & Record<string, unknown> | null} */
function mappedErrorKind(sqlstate) {
  switch (sqlstate) {
    case "23505": return { kind: "UniqueConstraintViolation" };
    case "23502": return { kind: "NullConstraintViolation" };
    case "23503": return { kind: "ForeignKeyConstraintViolation", constraint: { foreignKey: {} } };
    case "42P01": return { kind: "TableDoesNotExist" };
    case "42703": return { kind: "ColumnNotFound" };
    default: return null;
  }
}

/**
 * @param {string} script
 * @returns {string[]}
 */
function splitScript(script) {
  const statements = [];
  let start = 0;
  let index = 0;
  let firstWord = "";
  let word = "";
  let createTrigger = false;
  let bodyDepth = 0;
  const takeWord = () => {
    if (word === "") return;
    const upper = word.toUpperCase();
    if (firstWord === "") firstWord = upper;
    if (upper === "TRIGGER" && firstWord === "CREATE") createTrigger = true;
    else if (upper === "BEGIN" && createTrigger) bodyDepth += 1;
    else if (upper === "END" && createTrigger && bodyDepth > 0) bodyDepth -= 1;
    word = "";
  };
  while (index < script.length) {
    const character = script[index];
    if (character === "'") {
      takeWord();
      index += 1;
      while (index < script.length && script[index] !== "'") index += 1;
      index += 1;
    } else if (character === '"' || character === "`") {
      takeWord();
      const quote = character;
      index += 1;
      while (index < script.length && script[index] !== quote) index += 1;
      index += 1;
    } else if (character === "[") {
      takeWord();
      index += 1;
      while (index < script.length && script[index] !== "]") index += 1;
      index += 1;
    } else if (character === "-" && script[index + 1] === "-") {
      takeWord();
      while (index < script.length && script[index] !== "\n") index += 1;
    } else if (character === "/" && script[index + 1] === "*") {
      takeWord();
      index += 2;
      while (index < script.length && !(script[index] === "*" && script[index + 1] === "/")) index += 1;
      index = Math.min(index + 2, script.length);
    } else if (/[A-Za-z_]/u.test(character ?? "")) {
      word += character;
      index += 1;
    } else if (character === ";") {
      takeWord();
      if (!createTrigger || bodyDepth === 0) {
        const statement = script.slice(start, index + 1).trim();
        if (statement !== "" && statement !== ";") statements.push(statement);
        start = index + 1;
        firstWord = "";
        createTrigger = false;
        bodyDepth = 0;
      }
      index += 1;
    } else {
      takeWord();
      index += 1;
    }
  }
  takeWord();
  const rest = script.slice(start).trim();
  if (rest !== "") statements.push(rest);
  return statements;
}

const columnTypeEnum = Object.freeze({
  Int32: 0, Int64: 1, Float: 2, Double: 3, Numeric: 4, Boolean: 5, Character: 6, Text: 7,
  Date: 8, Time: 9, DateTime: 10, Json: 11, Enum: 12, Bytes: 13, Set: 14, Uuid: 15, UnknownNumber: 128,
});

/** @param {string} declType @returns {PrismaColumnType | null} */
function mapDeclType(declType) {
  switch (declType.toUpperCase()) {
    case "": return null;
    case "DECIMAL": return columnTypeEnum.Numeric;
    case "FLOAT": return columnTypeEnum.Float;
    case "DOUBLE":
    case "DOUBLE PRECISION":
    case "NUMERIC":
    case "REAL": return columnTypeEnum.Double;
    case "TINYINT":
    case "SMALLINT":
    case "MEDIUMINT":
    case "INT":
    case "INTEGER":
    case "SERIAL":
    case "INT2": return columnTypeEnum.Int32;
    case "BIGINT":
    case "UNSIGNED BIG INT":
    case "INT8": return columnTypeEnum.Int64;
    case "DATETIME":
    case "TIMESTAMP": return columnTypeEnum.DateTime;
    case "TIME": return columnTypeEnum.Time;
    case "DATE": return columnTypeEnum.Date;
    case "TEXT":
    case "CLOB":
    case "CHARACTER":
    case "VARCHAR":
    case "VARYING CHARACTER":
    case "NCHAR":
    case "NATIVE CHARACTER":
    case "NVARCHAR": return columnTypeEnum.Text;
    case "BLOB": return columnTypeEnum.Bytes;
    case "BOOLEAN": return columnTypeEnum.Boolean;
    case "JSONB": return columnTypeEnum.Json;
    default: return null;
  }
}

/** @param {readonly V1SqlColumn[]} columns @param {readonly (readonly V1SqlValue[])[]} rows @returns {PrismaColumnType[]} */
function columnTypes(columns, rows) {
  return columns.map((column, index) => {
    const declared = column.decltype === null ? null : mapDeclType(column.decltype);
    if (declared !== null) return declared;
    for (const row of rows) {
      const value = row[index];
      if (value === null || value === undefined) continue;
      if (typeof value === "string") return columnTypeEnum.Text;
      if (typeof value === "bigint" || value instanceof ExactDecimal) return columnTypeEnum.Int64;
      if (typeof value === "number") return columnTypeEnum.UnknownNumber;
      if (value instanceof Uint8Array) return columnTypeEnum.Bytes;
    }
    return columnTypeEnum.Int32;
  });
}

/** @param {V1SqlValue} value @param {PrismaColumnType} type @returns {string | number | Uint8Array | null} */
function mapValue(value, type) {
  if (value instanceof ExactDecimal) return String(value);
  if (typeof value === "number" && (type === columnTypeEnum.Int32 || type === columnTypeEnum.Int64) && !Number.isInteger(value))
    return Math.trunc(value);
  if ((typeof value === "number" || value instanceof ExactDecimal) && type === columnTypeEnum.DateTime) return new Date(Number(value)).toISOString();
  return value;
}

/** @param {unknown} arg @param {PrismaArgType | undefined} argType @returns {null | number | bigint | string | Uint8Array} */
function mapArgument(arg, argType) {
  if (arg === null || arg === undefined) return null;
  if (arg instanceof Date) return arg.toISOString().replace("Z", "+00:00");
  const scalarType = argType?.scalarType;
  if (typeof arg === "string" && scalarType === "bigint") return BigInt(arg);
  if (typeof arg === "string" && scalarType === "bytes") return Uint8Array.from(atob(arg), c => c.charCodeAt(0));
  if (typeof arg === "string" && scalarType === "decimal") return Number.parseFloat(arg);
  if (typeof arg === "number" || typeof arg === "bigint" || typeof arg === "string" || arg instanceof Uint8Array) return arg;
  return String(arg);
}

/**
 * A driver-adapter factory over one TerraBase database: `new PrismaTerraScale(client.database(name))`
 * passed to the ORM as its adapter. Queries run over HTTPS; interactive transactions run over the SQL WebSocket.
 */
export class PrismaTerraScale {
  /** @type {"sqlite"} */
  provider = "sqlite";
  /** @type {string} */
  adapterName = "@terrascale/sdk/prisma";
  /** @type {V1Database} */
  #database;

  /** @param {V1Database} database */
  constructor(database) {
    this.#database = database;
  }

  /** @returns {Promise<PrismaTerraScaleAdapter>} */
  async connect() {
    return new PrismaTerraScaleAdapter(this.#database);
  }
}

/** A driver adapter over one TerraBase database handle. */
export class PrismaTerraScaleAdapter {
  /** @type {"sqlite"} */
  provider = "sqlite";
  /** @type {string} */
  adapterName = "@terrascale/sdk/prisma";
  /** @type {V1Database} */
  #database;

  /** @param {V1Database} database */
  constructor(database) {
    this.#database = database;
  }

  /**
   * Runs one statement and returns its typed result set.
   * @param {PrismaSqlQuery} query
   * @returns {Promise<PrismaSqlResultSet>}
   */
  async queryRaw(query) {
    try {
      return sqlResultSet(await this.#database.execute(query.sql, sqlArguments(query)));
    } catch (error) {
      throw adapterError(error);
    }
  }

  /**
   * Runs one statement and returns the number of affected rows.
   * @param {PrismaSqlQuery} query
   * @returns {Promise<number>}
   */
  async executeRaw(query) {
    try {
      return (await this.#database.execute(query.sql, sqlArguments(query))).rowsAffected;
    } catch (error) {
      throw adapterError(error);
    }
  }

  /**
   * Runs a semicolon-separated script as one batched transaction.
   * @param {string} script
   */
  async executeScript(script) {
    try {
      await this.#database.batch(splitScript(script));
    } catch (error) {
      throw adapterError(error);
    }
  }

  /**
   * Opens a SQL WebSocket session and begins one interactive transaction of at most 5 seconds on it.
   * @param {PrismaIsolationLevel} [isolationLevel]
   * @returns {Promise<PrismaTerraScaleTransaction>}
   */
  async startTransaction(isolationLevel) {
    if (isolationLevel !== undefined && isolationLevel !== "SERIALIZABLE")
      throw new Error(`Unsupported isolation level: ${isolationLevel}. Only SERIALIZABLE is supported.`);
    const socket = await this.#database.connect();
    try {
      await socket.begin();
    } catch (error) {
      socket.close();
      throw adapterError(error);
    }
    return new PrismaTerraScaleTransaction(socket);
  }

  /** @returns {Promise<void>} */
  async dispose() {}
}

/** One interactive transaction over a dedicated SQL WebSocket session. */
export class PrismaTerraScaleTransaction {
  /** @type {"sqlite"} */
  provider = "sqlite";
  /** @type {string} */
  adapterName = "@terrascale/sdk/prisma";
  /** @type {{ usePhantomQuery: boolean }} */
  options = { usePhantomQuery: false };
  /** @type {V1SqlSocket} */
  #socket;

  /** @param {V1SqlSocket} socket */
  constructor(socket) {
    this.#socket = socket;
  }

  /**
   * Runs one statement in the transaction and returns its typed result set.
   * @param {PrismaSqlQuery} query
   * @returns {Promise<PrismaSqlResultSet>}
   */
  async queryRaw(query) {
    try {
      return sqlResultSet(await this.#socket.execute(query.sql, sqlArguments(query)));
    } catch (error) {
      throw adapterError(transactionError(error));
    }
  }

  /**
   * Runs one statement in the transaction and returns the number of affected rows.
   * @param {PrismaSqlQuery} query
   * @returns {Promise<number>}
   */
  async executeRaw(query) {
    try {
      return (await this.#socket.execute(query.sql, sqlArguments(query))).rowsAffected;
    } catch (error) {
      throw adapterError(transactionError(error));
    }
  }

  /**
   * @param {string} name
   * @returns {Promise<void>}
   */
  async createSavepoint(name) {
    await this.#socket.execute(`SAVEPOINT ${name}`);
  }

  /**
   * @param {string} name
   * @returns {Promise<void>}
   */
  async rollbackToSavepoint(name) {
    await this.#socket.execute(`ROLLBACK TO SAVEPOINT ${name}`);
  }

  /**
   * @param {string} name
   * @returns {Promise<void>}
   */
  async releaseSavepoint(name) {
    await this.#socket.execute(`RELEASE SAVEPOINT ${name}`);
  }

  /** Commits the transaction and closes the session. */
  async commit() {
    await this.#end("commit");
  }

  /** Rolls the transaction back and closes the session. */
  async rollback() {
    await this.#end("rollback");
  }

  /**
   * @param {"commit" | "rollback"} verb
   * @returns {Promise<void>}
   */
  async #end(verb) {
    try {
      await this.#socket[verb]();
    } catch (error) {
      this.#socket.close();
      throw adapterError(transactionError(error));
    }
    this.#socket.close();
  }
}
