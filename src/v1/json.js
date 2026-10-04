// Lossless JSON for the TerraBase v1 Documents API.
//
// TerraBase numbers are exact decimals with up to 128 significant digits
// (api-v1.md). Responses are parsed with JSON.parse source text access, so a
// number is returned as a JavaScript number only when that number denotes the
// same decimal value as the wire text; every other number becomes an
// ExactDecimal holding the original text. Requests are encoded with
// JSON.rawJSON, so ExactDecimal and bigint values reach the server unchanged.

const decimalGrammar = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/u;
const maximumIntegerExponent = 4096n;

/**
 * @typedef {object} CanonicalDecimal
 * @property {boolean} negative
 * @property {string} digits Significant digits without leading or trailing zeros; empty for zero.
 * @property {bigint} exponent The value is digits × 10^exponent.
 */

/**
 * @param {string} text
 * @returns {CanonicalDecimal | undefined}
 */
function canonicalDecimal(text) {
  const match = /^(-)?(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/u.exec(text);
  if (match === null) return undefined;
  const fraction = match[3] ?? "";
  let digits = `${match[2]}${fraction}`.replace(/^0+/u, "");
  let exponent = BigInt(match[4] ?? "0") - BigInt(fraction.length);
  if (digits === "") return { negative: false, digits: "", exponent: 0n };
  const trimmed = digits.replace(/0+$/u, "");
  exponent += BigInt(digits.length - trimmed.length);
  digits = trimmed;
  return { negative: match[1] === "-", digits, exponent };
}

/**
 * @param {string} left
 * @param {string} right
 * @returns {boolean}
 */
function sameDecimal(left, right) {
  const a = canonicalDecimal(left);
  const b = canonicalDecimal(right);
  return (
    a !== undefined &&
    b !== undefined &&
    a.negative === b.negative &&
    a.digits === b.digits &&
    a.exponent === b.exponent
  );
}

/** @returns {(text: string) => unknown} */
function rawJsonFactory() {
  const factory = /** @type {{ rawJSON?: unknown }} */ (
    /** @type {unknown} */ (JSON)
  ).rawJSON;
  if (typeof factory !== "function")
    throw new TypeError(
      "TerraBase v1 requires JSON.rawJSON and JSON.parse source text access (Node.js 22+, current browsers) for exact numbers.",
    );
  return /** @type {(text: string) => unknown} */ (factory);
}

/**
 * An exact decimal number as it travels on the wire. Values that a JavaScript
 * number cannot represent exactly (large integers, long fractions) are
 * returned as ExactDecimal; it serializes back to the identical JSON text.
 */
export class ExactDecimal {
  /** @type {string} */
  #text;

  /** @param {string} text A JSON number literal. */
  constructor(text) {
    if (typeof text !== "string" || !decimalGrammar.test(text))
      throw new TypeError("ExactDecimal requires a JSON number literal.");
    this.#text = text;
    Object.freeze(this);
  }

  /**
   * @param {ExactDecimal | bigint | number | string} value
   * @returns {ExactDecimal}
   */
  static from(value) {
    if (value instanceof ExactDecimal) return value;
    if (typeof value === "bigint") return new ExactDecimal(value.toString());
    if (typeof value === "number") {
      if (!Number.isFinite(value))
        throw new TypeError("TerraBase numbers must be finite.");
      return new ExactDecimal(String(value));
    }
    return new ExactDecimal(value);
  }

  /** @returns {string} The exact JSON number text. */
  toString() {
    return this.#text;
  }

  /** @returns {unknown} A raw JSON value that serializes as the exact text. */
  toJSON() {
    return rawJsonFactory()(this.#text);
  }

  /** @returns {number} The nearest JavaScript number; may lose precision. */
  toNumber() {
    return Number(this.#text);
  }

  /** @returns {boolean} */
  isInteger() {
    return /** @type {CanonicalDecimal} */ (canonicalDecimal(this.#text)).exponent >= 0n;
  }

  /**
   * @returns {bigint} The exact integer value.
   * @throws {RangeError} When the value is not an integer.
   */
  toBigInt() {
    const value = /** @type {CanonicalDecimal} */ (canonicalDecimal(this.#text));
    if (value.exponent < 0n)
      throw new RangeError(`${this.#text} is not an integer.`);
    if (value.exponent > maximumIntegerExponent)
      throw new RangeError(`${this.#text} is too large to expand.`);
    if (value.digits === "") return 0n;
    const magnitude = BigInt(
      `${value.digits}${"0".repeat(Number(value.exponent))}`,
    );
    return value.negative ? -magnitude : magnitude;
  }

  /**
   * Exact decimal equality: 1, 1.0 and 1e0 are equal.
   * @param {ExactDecimal | bigint | number | string} other
   * @returns {boolean}
   */
  equals(other) {
    return sameDecimal(this.#text, ExactDecimal.from(other).toString());
  }

  /** @returns {string} */
  [Symbol.for("nodejs.util.inspect.custom")]() {
    return `ExactDecimal(${this.#text})`;
  }
}

/**
 * A decoded JSON value. Numbers are `number` when exact, else ExactDecimal.
 * @typedef {null | boolean | number | string | ExactDecimal | JsonArray | JsonObject} JsonValue
 */
/** @typedef {JsonValue[]} JsonArray */
/** @typedef {{ [key: string]: JsonValue }} JsonObject */

/**
 * A JSON value accepted in requests: bigint and ExactDecimal encode exactly.
 * @typedef {null | boolean | number | bigint | string | ExactDecimal | JsonInputArray | JsonInputObject} JsonInput
 */
/** @typedef {readonly JsonInput[]} JsonInputArray */
/** @typedef {{ readonly [key: string]: JsonInput }} JsonInputObject */

/**
 * A JSON.parse reviver that maps every number through `map(source text)`.
 * @param {(source: unknown) => unknown} map
 * @returns {(key: string, value: unknown) => unknown}
 */
function sourceReviver(map) {
  /**
   * @param {string} _key
   * @param {unknown} value
   * @param {{ source?: unknown }} [context]
   * @returns {unknown}
   */
  function revive(_key, value, context) {
    return typeof value === "number" ? map(context?.source) ?? value : value;
  }
  return revive;
}

let sourceAccess = /** @type {boolean | undefined} */ (undefined);

/** @returns {void} */
function requireSourceAccess() {
  if (sourceAccess === undefined) {
    let seen;
    JSON.parse("1", sourceReviver((source) => {
      seen = source;
    }));
    sourceAccess = seen === "1" && typeof (/** @type {{ rawJSON?: unknown }} */ (/** @type {unknown} */ (JSON)).rawJSON) === "function";
  }
  if (!sourceAccess)
    throw new TypeError(
      "TerraBase v1 requires JSON.rawJSON and JSON.parse source text access (Node.js 22+, current browsers) for exact numbers.",
    );
}

/**
 * @param {string} source
 * @returns {number | ExactDecimal}
 */
function exactNumber(source) {
  const value = Number(source);
  return Number.isFinite(value) && sameDecimal(source, String(value))
    ? value
    : new ExactDecimal(source);
}

/**
 * Parses JSON text without losing number precision.
 * @param {string} text
 * @returns {JsonValue}
 */
export function parseExactJson(text) {
  requireSourceAccess();
  return JSON.parse(text, sourceReviver((source) => {
    if (typeof source !== "string")
      throw new TypeError("JSON number source text is unavailable.");
    return exactNumber(source);
  }));
}

/**
 * @param {unknown} value
 * @param {number} depth
 * @param {string} where
 * @returns {void}
 */
function assertJsonInput(value, depth, where) {
  if (depth > 64) throw new TypeError(`${where} is nested too deeply.`);
  if (
    value === null ||
    typeof value === "boolean" ||
    typeof value === "string" ||
    typeof value === "bigint" ||
    value instanceof ExactDecimal
  )
    return;
  if (typeof value === "number") {
    if (!Number.isFinite(value))
      throw new TypeError(`${where} contains a non-finite number.`);
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) assertJsonInput(item, depth + 1, where);
    return;
  }
  if (typeof value === "object") {
    const prototype = Object.getPrototypeOf(value);
    if (prototype !== Object.prototype && prototype !== null)
      throw new TypeError(`${where} contains a non-plain object.`);
    for (const item of Object.values(value)) {
      if (item === undefined)
        throw new TypeError(`${where} contains an undefined member.`);
      assertJsonInput(item, depth + 1, where);
    }
    return;
  }
  throw new TypeError(`${where} contains a value that is not JSON.`);
}

/**
 * Encodes a request value exactly. bigint and ExactDecimal keep every digit.
 * @param {unknown} value
 * @param {string} where
 * @returns {string}
 */
export function stringifyExactJson(value, where) {
  requireSourceAccess();
  assertJsonInput(value, 0, where);
  const raw = rawJsonFactory();
  return JSON.stringify(value, (_key, item) =>
    typeof item === "bigint" ? raw(item.toString()) : item,
  );
}

/**
 * @param {unknown} value
 * @param {string} where
 * @returns {void}
 */
export function assertJsonObject(value, where) {
  if (
    typeof value !== "object" ||
    value === null ||
    Array.isArray(value) ||
    value instanceof ExactDecimal
  )
    throw new TypeError(`${where} must be a JSON object.`);
  assertJsonInput(value, 0, where);
}

/**
 * @param {unknown} value
 * @returns {value is JsonObject}
 */
export function isJsonObject(value) {
  return (
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value) &&
    !(value instanceof ExactDecimal)
  );
}
