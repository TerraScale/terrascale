import { describe, expect, it } from "vitest";
import { blake3 } from "./blake3.js";

describe("blake3", () => {
  it("matches the published empty-input vector", () => {
    expect(blake3(new Uint8Array())).toBe(
      "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
    );
  });

  it("matches the published abc vector", () => {
    expect(blake3(new TextEncoder().encode("abc"))).toBe(
      "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85",
    );
  });
});
