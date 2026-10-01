#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { currentProducerOpenApi } from "./openapi-contract-paths.js";
/**
 * @typedef {object} PinnedRoute
 * @property {string} method
 * @property {string} path
 * @property {string} [request]
 * @property {string} response
 */

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
/** @type {{ routes: PinnedRoute[] }} */
const pin = JSON.parse(
  await readFile(path.join(root, "sdk-current-contract.json"), "utf8"),
);
const spec = JSON.parse(await readFile(await currentProducerOpenApi(), "utf8"));
const names = ["read", "query", "subscription", "commit", "transactionStatus"];
const routes = pin.routes.map((route, index) => {
  const operation = spec.paths[route.path]?.[route.method.toLowerCase()];
  if (operation === undefined || typeof operation.operationId !== "string")
    throw new Error(`Missing producer route ${route.path}.`);
  if (
    operation.responses["200"].content["application/json"].schema.$ref !==
    `#/components/schemas/${route.response}`
  )
    throw new Error(`Native response drift ${route.path}.`);
  if (
    route.request !== undefined &&
    operation.requestBody.content["application/json"].schema.$ref !==
      `#/components/schemas/${route.request}`
  )
    throw new Error(`Native request drift ${route.path}.`);
  return {
    base: "native",
    producer: "terrabase",
    client: "TerraBaseClient",
    operation: names[index],
    operationId: operation.operationId,
    routeName: `database.${names[index]}`,
    method: route.method,
    pathTemplate: route.path,
    requestType: route.request ?? "void",
    responseType: route.response,
  };
});
const manifest = {
  manifestVersion: 2,
  contractVersion: "terrabase-client-v1",
  routeCount: routes.length,
  routes,
};
const output = `${JSON.stringify(manifest, null, 2)}\n`;
const target = path.join(root, "sdk-route-manifest.json");
if (process.argv.includes("--check")) {
  if ((await readFile(target, "utf8")) !== output)
    throw new Error(
      "sdk-route-manifest.json differs from actual current producer routes.",
    );
} else await writeFile(target, output);
console.log(`Native route manifest verified (${routes.length} routes).`);
