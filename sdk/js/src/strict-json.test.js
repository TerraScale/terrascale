import { expect, test } from "vitest";
import { parseStrictJson } from "./strict-json.js";

  test("bounded JSON rejects duplicate members, invalid UTF-8 and excessive syntax depth", () => {
    /** @type {(value: string) => Uint8Array} */
    const bytes = value => new TextEncoder().encode(value);
    expect(parseStrictJson(bytes('{"a":[true,false,null,-1.2e+3,"escaped\\n"]}'))).toEqual({ a: [true, false, null, -1200, "escaped\n"] });
    expect(() => parseStrictJson(bytes('{"state":"absent","st\\u0061te":"value"}'))).toThrow(TypeError);
    expect(() => parseStrictJson(new Uint8Array([0xff]))).toThrow(TypeError);
    expect(() => parseStrictJson(bytes("[".repeat(66) + "0" + "]".repeat(66)))).toThrow(TypeError);
  });
