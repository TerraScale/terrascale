const maximumBodyBytes = 4 * 1024 * 1024;

/**
 * Reject duplicate JSON members and unbounded syntax before typed response decoding.
 * @param {Uint8Array} bytes
 * @returns {unknown}
 */
export function parseStrictJson(bytes) {
  if (bytes.length < 1 || bytes.length > maximumBodyBytes) throw new TypeError("JSON response exceeds its byte limit.");
  const input = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  let offset = 0;
  /** @type {() => void} */
  const whitespace = () => { while (/[ \t\r\n]/u.test(input[offset] ?? "x")) offset++; };
  /** @type {() => never} */
  const fail = () => { throw new TypeError("Response contains malformed or duplicate JSON members."); };
  /** @type {() => string} */
  const string = () => {
    const start = offset++;
    while (offset < input.length) {
      const code = input.charCodeAt(offset);
      if (input[offset] === '"') {
        offset++;
        /** @type {unknown} */
        const parsed = JSON.parse(input.slice(start, offset));
        if (typeof parsed !== "string") return fail();
        return parsed;
      }
      if (code < 32) return fail();
      if (input[offset++] === "\\") {
        const escaped = input[offset++];
        if (escaped === "u") {
          if (!/^[0-9a-fA-F]{4}$/u.test(input.slice(offset, offset + 4))) return fail();
          offset += 4;
        } else if (escaped === undefined || !'"\\/bfnrt'.includes(escaped)) return fail();
      }
    }
    return fail();
  };
  /** @type {(depth: number) => void} */
  const value = (depth) => {
    if (depth > 64) fail();
    whitespace();
    if (input[offset] === '"') { string(); return; }
    if (input[offset] === "{" || input[offset] === "[") {
      const map = input[offset++] === "{";
      const end = map ? "}" : "]";
      /** @type {Set<string>} */
      const keys = new Set();
      whitespace();
      if (input[offset] === end) { offset++; return; }
      while (offset < input.length) {
        if (map) {
          whitespace();
          if (input[offset] !== '"') fail();
          const key = string();
          if (keys.has(key)) fail();
          keys.add(key);
          whitespace();
          if (input[offset++] !== ":") fail();
        }
        value(depth + 1);
        whitespace();
        if (input[offset] === end) { offset++; return; }
        if (input[offset++] !== ",") fail();
      }
      fail();
    }
    for (const literal of ["true", "false", "null"]) {
      if (input.slice(offset, offset + literal.length) === literal) { offset += literal.length; return; }
    }
    const number = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/uy;
    number.lastIndex = offset;
    if (number.exec(input) === null) fail();
    offset = number.lastIndex;
  };
  value(0); whitespace();
  if (offset !== input.length) fail();
  return /** @type {unknown} */ (JSON.parse(input));
}
