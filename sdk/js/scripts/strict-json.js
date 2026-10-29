/** @type {ReadonlySet<string | undefined>} */
const whitespace = new Set([" ", "\t", "\r", "\n"]);

/**
 * Parses JSON after rejecting duplicate keys, invalid UTF-8, trailing data, and
 * documents that exceed the size or nesting limits.
 *
 * @param {Uint8Array | string} bytes
 * @param {string} name
 * @param {{ maximumBytes?: number, maximumDepth?: number }} [options]
 * @returns {any}
 */
export function parseStrictJson(bytes, name, { maximumBytes = 4 * 1024 * 1024, maximumDepth = 32 } = {}) {
  const buffer = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes);
  if (buffer.length < 1 || buffer.length > maximumBytes) {
    throw new Error(`${name} is empty or exceeds its size limit.`);
  }
  const text = buffer.toString("utf8");
  if (!Buffer.from(text, "utf8").equals(buffer)) throw new Error(`${name} is not valid UTF-8.`);

  let offset = 0;
  parseValue(0);
  skipWhitespace();
  if (offset !== text.length) fail("contains trailing data");

  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${name} contains malformed JSON.`);
  }

  /**
   * @param {number} depth
   * @returns {void}
   */
  function parseValue(depth) {
    if (depth > maximumDepth) fail("exceeds its nesting limit");
    skipWhitespace();
    const character = text[offset];
    if (character === "{") parseObject(depth + 1);
    else if (character === "[") parseArray(depth + 1);
    else if (character === '"') parseString();
    else if (character === "t") parseLiteral("true");
    else if (character === "f") parseLiteral("false");
    else if (character === "n") parseLiteral("null");
    else parseNumber();
  }

  /**
   * @param {number} depth
   * @returns {void}
   */
  function parseObject(depth) {
    offset += 1;
    skipWhitespace();
    /** @type {Set<string>} */
    const names = new Set();
    if (text[offset] === "}") {
      offset += 1;
      return;
    }
    while (offset < text.length) {
      skipWhitespace();
      if (text[offset] !== '"') fail("has a non-string object key");
      const key = parseString();
      if (names.has(key)) fail(`contains duplicate object key ${JSON.stringify(key)}`);
      names.add(key);
      skipWhitespace();
      if (text[offset] !== ":") fail("has an object key without a value");
      offset += 1;
      parseValue(depth);
      skipWhitespace();
      if (text[offset] === "}") {
        offset += 1;
        return;
      }
      if (text[offset] !== ",") fail("has a malformed object");
      offset += 1;
    }
    fail("has an unterminated object");
  }

  /**
   * @param {number} depth
   * @returns {void}
   */
  function parseArray(depth) {
    offset += 1;
    skipWhitespace();
    if (text[offset] === "]") {
      offset += 1;
      return;
    }
    while (offset < text.length) {
      parseValue(depth);
      skipWhitespace();
      if (text[offset] === "]") {
        offset += 1;
        return;
      }
      if (text[offset] !== ",") fail("has a malformed array");
      offset += 1;
    }
    fail("has an unterminated array");
  }

  /** @returns {string} */
  function parseString() {
    const start = offset;
    offset += 1;
    while (offset < text.length) {
      const character = text[offset];
      if (character === '"') {
        offset += 1;
        try {
          return JSON.parse(text.slice(start, offset));
        } catch {
          fail("contains an invalid JSON string");
        }
      }
      if (character === "\\") {
        offset += 1;
        if (text[offset] === "u") {
          if (!/^[0-9a-fA-F]{4}$/u.test(text.slice(offset + 1, offset + 5))) fail("contains an invalid Unicode escape");
          offset += 5;
          continue;
        }
        if (!/["\\/bfnrt]/u.test(text[offset] ?? "")) fail("contains an invalid string escape");
        offset += 1;
      } else {
        if (character === undefined || character.charCodeAt(0) < 0x20) fail("contains a control character in a string");
        offset += 1;
      }
    }
    fail("has an unterminated string");
  }

  /** @returns {void} */
  function parseNumber() {
    const match = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/uy;
    match.lastIndex = offset;
    const result = match.exec(text);
    if (result === null) fail("contains an invalid value");
    offset = match.lastIndex;
  }

  /**
   * @param {string} literal
   * @returns {void}
   */
  function parseLiteral(literal) {
    if (text.slice(offset, offset + literal.length) !== literal) fail("contains an invalid value");
    offset += literal.length;
  }

  /** @returns {void} */
  function skipWhitespace() {
    while (whitespace.has(text[offset])) offset += 1;
  }

  /**
   * @param {string} reason
   * @returns {never}
   */
  function fail(reason) {
    throw new Error(`${name} ${reason}.`);
  }
}

/**
 * Requires a plain object whose own keys are exactly the expected set.
 *
 * @template T
 * @param {T} value
 * @param {readonly string[]} expected
 * @param {string} name
 * @returns {T}
 */
export function requireExactKeys(value, expected, name) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`${name} must be an object.`);
  }
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (actual.length !== wanted.length || actual.some((key, index) => key !== wanted[index])) {
    throw new Error(`${name} has missing or unknown fields.`);
  }
  return value;
}
