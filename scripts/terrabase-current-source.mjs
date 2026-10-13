import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

const directory = new URL("../contracts/terrabase-current-source/", import.meta.url);
export const currentSourcePin = JSON.parse(await readFile(new URL("pin.json", directory), "utf8"));
const sources = new Map();
for (const [name, input] of Object.entries(currentSourcePin.inputs)) {
  const bytes = await readFile(new URL(name, directory));
  assert.equal(createHash("sha256").update(bytes).digest("hex"), input.sha256, `${name}: current producer source digest mismatch.`);
  sources.set(name, bytes.toString("utf8"));
}

// Evaluate only C# string constants and concatenations, never producer code.
const expressions = new Map();
for (const [name, source] of sources) {
  const owner = name.split("/").at(-1).replace(/\.cs$/u, "");
  for (const match of source.matchAll(/(?:public|internal|private) const string (\w+) = ([^;]+);/gu))
    expressions.set(`${owner}.${match[1]}`, match[2]);
}
/** @param {string} expression @param {string} owner @returns {string} */
function constant(expression, owner) {
  return expression.split(/\s+\+\s+/u).map(part => {
    const text = part.trim();
    if (text.startsWith('"')) return JSON.parse(text);
    const key = text.includes(".") ? text : `${owner}.${text}`;
    assert.ok(expressions.has(key), `Unresolved producer route/media constant ${key}.`);
    return constant(expressions.get(key), key.slice(0, key.lastIndexOf(".")));
  }).join("");
}
const scopes = sources.get("src/TerraBase/Features/ApiKeys/IApiKeyStore.cs");
const statusNames = new Map([[200, "Status200OK"], [201, "Status201Created"], [204, "Status204NoContent"]]);

/** @param {readonly import("../src/v1/routes.js").V1Route[]} routes */
export function verifyCurrentSourceRoutes(routes) {
  const evidence = [];
  for (const route of routes) {
    const sourcePath = currentSourcePin.routes[route.operation];
    assert.ok(sourcePath, `${route.operation}: no current source-backed route; historical shape is separately inventoried.`);
    const source = sources.get(sourcePath);
    const owner = sourcePath.split("/").at(-1).replace(/\.cs$/u, "");
    const registration = source.match(/\n\s+(Get|Put|Post|Patch|Delete)\(([^;]+)\);/u);
    assert.ok(registration, `${route.operation}: producer method/path registration missing.`);
    assert.equal(route.method, registration[1].toUpperCase(), `${route.operation}: producer method drift.`);
    assert.equal(route.pathTemplate, constant(registration[2], owner), `${route.operation}: producer path drift.`);
    const policy = source.match(/RequireAuthorization\(([^)]+)\)/u)?.[1];
    assert.ok(policy, `${route.operation}: producer scope policy missing.`);
    let permission;
    if (policy.startsWith("ApiKeyScopes.")) {
      permission = scopes.match(new RegExp(`const string ${policy.slice("ApiKeyScopes.".length)} = "([^"]+)"`))?.[1];
    } else {
      assert.equal(policy, "UserAuthExtensions.PolicyName", `${route.operation}: unrecognized producer authentication policy.`);
      assert.ok(source.includes("token.Reaches("), `${route.operation}: project/database narrowing missing.`);
      permission = source.match(/!token.Grants\("([^"]+)"\)/u)?.[1];
      assert.ok(permission, `${route.operation}: in-handler scope check missing.`);
    }
    assert.equal(route.permission, permission, `${route.operation}: producer scope drift.`);
    const nameExpression = source.match(/\n\s+Name\(([^)]+)\);/u)?.[1];
    assert.ok(nameExpression, `${route.operation}: producer operation name missing.`);
    const producerOperationId = constant(nameExpression, owner);
    const explicitMedia = [...source.matchAll(/new RequestBodyMetadata\(typeof\([^)]*\), ([^)]+)\)/gu)]
      .map(match => constant(match[1], owner));
    const requestMediaTypes = explicitMedia.length > 0 ? explicitMedia : /Endpoint<|RequestJson\.Read</u.test(source) ? ["application/json"] : [];
    assert.deepEqual([...route.requestMediaTypes].sort(), requestMediaTypes.sort(), `${route.operation}: producer request media drift.`);
    const responseMediaType = route.operation === "deleteDatabase" ? "" :
      source.includes('"text/event-stream"') ? "text/event-stream" : "application/json";
    assert.equal(route.responseMediaType, responseMediaType, `${route.operation}: producer response media drift.`);
    const successStatuses = [...statusNames].filter(([status, name]) => status === 200 ?
      /Produces<(?!ErrorBody)[^\n;]+>\(\)/u.test(source) || source.includes("Status200OK") : source.includes(name)).map(([status]) => status);
    assert.deepEqual([...route.successStatuses].sort(), successStatuses.sort(), `${route.operation}: producer success-status drift.`);
    assert.equal(route.idempotencyKey, "none", `${route.operation}: producer has no Idempotency-Key contract.`);
    evidence.push({ operation: route.operation, producerOperationId, sourcePath, sha256: currentSourcePin.inputs[sourcePath].sha256,
      method: route.method, pathTemplate: route.pathTemplate, authorizationPolicy: policy, permission, requestMediaTypes, responseMediaType, successStatuses });
  }
  return evidence;
}
