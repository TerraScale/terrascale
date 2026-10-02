// The v1 route manifest against the frozen terrabase contract documents.
// The contract documents are the vendored copies in contracts/terrabase-v1,
// verified against the pinned digests; TERRABASE_V1_CONTRACT_DIR (a docs
// directory) or TERRABASE_REPO (a terrabase checkout, read at the pinned
// revision with git show) override them.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, test } from "vitest";
import { verifyRouteDeclarations } from "../scripts/v1-contract.js";
import { v1Routes } from "../src/v1/routes.js";

const manifest = JSON.parse(readFileSync(new URL("../sdk-v1-route-manifest.json", import.meta.url), "utf8"));
const names = ["api-v1.md", "query-v1.md", "shapes-v1.md"];

/** @returns {Record<string, string>} */
function contractTexts() {
  const directory = process.env.TERRABASE_V1_CONTRACT_DIR;
  const repository = process.env.TERRABASE_REPO;
  /** @type {(name: string) => string} */
  let read = (name) => readFileSync(new URL(`../contracts/terrabase-v1/${name}`, import.meta.url), "utf8");
  if (directory !== undefined && directory !== "") read = (name) => readFileSync(path.join(directory, name), "utf8");
  else if (repository !== undefined && repository !== "")
    read = (name) => execFileSync("git", ["-C", repository, "show", `${manifest.contractSource.revision}:docs/${name}`], { encoding: "utf8" });
  return Object.fromEntries(names.map((name) => [name, read(name)]));
}
const texts = contractTexts();

describe("v1 route manifest", () => {
  test("the route table the client sends from is immutable", () => {
    for (const route of v1Routes) {
      expect(Object.isFrozen(route)).toBe(true);
      for (const list of [route.requestMediaTypes, route.successStatuses, route.contractText]) expect(Object.isFrozen(list)).toBe(true);
    }
    expect(() => /** @type {number[]} */ (/** @type {unknown} */ (v1Routes[0]?.successStatuses)).push(500)).toThrow(TypeError);
  });

  test("the contract documents match the pinned digests", () => {
    for (const name of names)
      expect(createHash("sha256").update(/** @type {string} */ (texts[name])).digest("hex"), name).toBe(manifest.contractSource.documents[name]);
  });

  test("every route's actual method and path are declared in its contract document", () => {
    const documents = texts;
    verifyRouteDeclarations(v1Routes, documents);
    // Changing what the transport sends, without touching contractText, fails.
    const query = /** @type {import("../src/v1/routes.js").V1Route} */ (v1Routes.find((route) => route.operation === "queryDocuments"));
    const get = /** @type {import("../src/v1/routes.js").V1Route} */ (v1Routes.find((route) => route.operation === "getDocument"));
    for (const mutated of [
      { ...query, pathTemplate: "/v1/databases/{db}/collections/{collection}/search" },
      { ...query, method: "PUT" },
      { ...get, pathTemplate: get.pathTemplate.replace("/documents/", "/docs/") },
      { ...get, method: "HEAD" },
      { ...get, contract: "shapes-v1.md" },
    ]) expect(() => verifyRouteDeclarations([mutated], documents), `${mutated.method} ${mutated.pathTemplate}`).toThrow(/is not declared/u);
  });
});
