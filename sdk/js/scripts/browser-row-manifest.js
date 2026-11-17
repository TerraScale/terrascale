import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { rowRoutes } from "../src/v1/rows.js";

const sources = new Map();
for (const [owner, path] of [
  ["DatabaseRoute", "src/TerraBase/Hosting/DatabaseRoute.cs"],
  ["RowsRoute", "src/TerraBase/Features/Rows/RowsRoute.cs"],
]) {
  const text = await readFile(new URL("../../../" + path, import.meta.url), "utf8");
  for (const match of text.matchAll(/const string (\w+) = ([^;]+);/gu)) sources.set(owner + "." + match[1], match[2]);
}
/** @param {string} expression @returns {string} */
function pathOf(expression) {
  return expression.split(/\s+\+\s+/u).map(part => part.startsWith('"') ? JSON.parse(part) :
    pathOf(sources.get(part.includes(".") ? part : "RowsRoute." + part))).join("");
}
const target = new URL("../sdk-public-v1-route-manifest.json", import.meta.url);
/** @type {{ producerRevision: string; routes: { operation: string; path: string; method: string; [key: string]: unknown }[] }} */
const manifest = JSON.parse(await readFile(target, "utf8"));
/** @type {Set<string>} */
const operations = new Set(rowRoutes.map(route => route.operation));
manifest.routes = manifest.routes.filter(route => !operations.has(route.operation));
for (const route of rowRoutes) {
  const source = route.operation === "getRow" ? "src/TerraBase/Features/Shapes/GetRowEndpoint.cs" :
    "src/TerraBase/Features/Rows/" + ({ createRow: "CreateRowEndpoint", updateRow: "UpdateRowEndpoint",
      deleteRow: "DeleteRowEndpoint", batchRows: "BatchRowsEndpoint" })[route.operation] + ".cs";
  const text = await readFile(new URL("../../../" + source, import.meta.url), "utf8");
  assert.ok(text.includes('Name("' + route.operation + '")'), route.operation + ": producer operation drift");
  const registration = text.match(/\n\s+(?:Get|Post|Patch|Delete)\(([^;]+)\);/u);
  const expression = registration?.[1];
  assert.ok(expression, route.operation + ": producer registration missing");
  assert.equal(pathOf(expression), route.pathTemplate, route.operation + ": producer path drift");
  assert.ok(text.includes(({ GET: "Get", POST: "Post", PATCH: "Patch", DELETE: "Delete" })[route.method] + "("),
    route.operation + ": producer method drift");
  manifest.routes.push({ method: route.method, path: route.pathTemplate, operation: route.operation,
    scopes: [route.permission], requestMediaTypes: route.requestMediaTypes, statuses: route.successStatuses,
    responseMediaType: route.responseMediaType, source, request: route.requestMediaTypes.length ? "json" : "none",
    sourceSha256: createHash("sha256").update(text).digest("hex") });
}
manifest.routes.sort((a, b) => a.path.localeCompare(b.path, "en") || a.method.localeCompare(b.method, "en"));
const output = '{\n "producerRevision": ' + JSON.stringify(manifest.producerRevision) + ',\n "routes": [\n'
  + manifest.routes.map(route => "  " + JSON.stringify(route)).join(",\n") + "\n ]\n}\n";
if (process.argv.includes("--check")) assert.equal(await readFile(target, "utf8"), output,
  "Run npm run generate:v1-route-manifest.");
else await writeFile(target, output);
