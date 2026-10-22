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
  let end = digits.length;
  while (digits[end - 1] === "0") end--;
  exponent += BigInt(digits.length - end);
  digits = digits.slice(0, end);
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
 * C# ExactNumber bounds: leading zeros do not count, trailing zeros do.
 * @param {string} text
 * @param {string} where
 * @returns {void}
 */
function requireProducerNumber(text, where) {
  if (!decimalGrammar.test(text)) throw new TypeError(`${where} contains a non-finite number.`);
  const [mantissa = "", rawExponent = "0"] = text.split(/[eE]/u);
  const exponent = BigInt(rawExponent);
  const fraction = mantissa.split(".")[1] ?? "";
  const digits = mantissa.replace(/[-.]/gu, "").replace(/^0+/u, "");
  const magnitude = exponent - BigInt(fraction.length) + BigInt(digits.length) - 1n;
  if (exponent < -1_000_000n || exponent > 1_000_000n || digits.length > 128 ||
    (digits !== "" && (magnitude < -307n || magnitude > 307n)))
    throw new TypeError(`${where} numbers have at most 128 significant digits and a magnitude of zero or 1e-307 to below 1e308.`);
}

/**
 * Validates a document, merge patch or patch operand against C# RequestJson limits.
 * @param {unknown} value
 * @param {string} where
 * @returns {void}
 */
export function assertJsonValue(value, where) {
  assertJsonInput(value, 0, where, { nodes: 0 });
}

/**
 * @param {unknown} value
 * @param {number} depth
 * @param {string} where
 * @param {{ nodes: number }} [budget] Document limits; request envelopes are validated separately.
 * @returns {void}
 */
function assertJsonInput(value, depth, where, budget) {
  if (budget !== undefined && ++budget.nodes > 4096)
    throw new TypeError(`${where} holds at most 4096 values.`);
  if (depth > 64) throw new TypeError(`${where} is nested too deeply.`);
  if (value === null || typeof value === "boolean") return;
  if (typeof value === "string") {
    if (/[\uD800-\uDFFF]/u.test(value)) throw new TypeError(`${where} contains a lone surrogate.`);
    return;
  }
  if (typeof value === "bigint" || typeof value === "number" || value instanceof ExactDecimal) {
    requireProducerNumber(String(value), where);
    return;
  }
  if (budget !== undefined && typeof value === "object" && depth >= 16)
    throw new TypeError(`${where} nests at most 16 levels.`);
  if (Array.isArray(value)) {
    for (const item of value) assertJsonInput(item, depth + 1, where, budget);
    return;
  }
  if (typeof value === "object") {
    const prototype = Object.getPrototypeOf(value);
    if (prototype !== Object.prototype && prototype !== null)
      throw new TypeError(`${where} contains a non-plain object.`);
    for (const [key, item] of Object.entries(value)) {
      if (/[\uD800-\uDFFF]/u.test(key)) throw new TypeError(`${where} contains a lone surrogate member name.`);
      if (item === undefined)
        throw new TypeError(`${where} contains an undefined member.`);
      assertJsonInput(item, depth + 1, where, budget);
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
  assertJsonValue(value, where);
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
