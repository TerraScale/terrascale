#!/usr/bin/env node
// Generates or checks sdk-v1-route-manifest.json from the route table the v1
// client builds its URLs from (src/v1/routes.js).
//
// The v1 producer publishes its contract as the frozen terrabase documents
// docs/api-v1.md, docs/query-v1.md and docs/shapes-v1.md; tb-server does not
// serve /v1/openapi.json for these routes yet. The manifest pins those
// documents by SHA-256. With TERRABASE_V1_CONTRACT_DIR set to the absolute path
// of the terrabase docs directory, --check also verifies the pinned digests and
// that every route is declared, literally, in its contract document. When the
// producer OpenAPI ships, this check moves to OpenAPI parity like
// sdk-route-manifest.json.
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { v1Routes } from "../src/v1/routes.js";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const target = path.join(root, "sdk-v1-route-manifest.json");

const contractSource = {
  producer: "terrabase",
  revision: "7bc74f9d224167ff2dc6b815965305feca210c00",
  status: "frozen contract documents; producer OpenAPI and runtime qualification pending",
  documents: {
    "api-v1.md": "04f494248d8f3221675b26e997f8765b957e2b3f1bb19e6fa091f97b499420d5",
    "query-v1.md": "c5b0bdf07bd37b62c954526bc17af7cfbf22ed30f5545c5e23da68d2cb3a3e39",
    "shapes-v1.md": "be919ff760e5793eec5b9e249c4b1e18d0b306aecf418d1a3cd6c48b0b244106",
  },
};

const manifest = {
  manifestVersion: 1,
  contractVersion: "terrabase-documents-v1",
  contractSource,
  routeCount: v1Routes.length,
  routes: v1Routes.map((route) => ({
    producer: "terrabase",
    client: "TerraBaseV1Client",
    operation: route.operation,
    method: route.method,
    pathTemplate: route.pathTemplate,
    permission: route.permission,
    requestMediaTypes: route.requestMediaTypes,
    responseMediaType: route.responseMediaType,
    successStatuses: route.successStatuses,
    idempotencyKey: route.idempotencyKey,
    replaySafe: route.replaySafe,
    contract: route.contract,
    contractText: route.contractText,
  })),
};
const output = `${JSON.stringify(manifest, null, 2)}\n`;

if (process.argv.includes("--check")) {
  if ((await readFile(target, "utf8")) !== output)
    throw new Error("sdk-v1-route-manifest.json differs from src/v1/routes.js; run npm run generate:v1-route-manifest.");
  const directory = process.env.TERRABASE_V1_CONTRACT_DIR;
  if (directory === undefined) {
    console.log(`v1 route manifest matches the client (${v1Routes.length} routes); contract documents not supplied, digest parity skipped.`);
  } else {
    if (!path.isAbsolute(directory)) throw new Error("TERRABASE_V1_CONTRACT_DIR must be an absolute path.");
    /** @type {Record<string, string>} */
    const texts = {};
    for (const [name, digest] of Object.entries(contractSource.documents)) {
      const bytes = await readFile(path.join(directory, name));
      if (createHash("sha256").update(bytes).digest("hex") !== digest)
        throw new Error(`${name} differs from the pinned v1 contract digest.`);
      texts[name] = bytes.toString("utf8");
    }
    for (const route of v1Routes)
      for (const literal of route.contractText)
        if (!(texts[route.contract] ?? "").includes(literal))
          throw new Error(`${route.operation} is not declared in ${route.contract} as ${literal}.`);
    console.log(`v1 route manifest matches the client and the pinned contract documents (${v1Routes.length} routes).`);
  }
} else {
  await writeFile(target, output);
  console.log(`Wrote sdk-v1-route-manifest.json (${v1Routes.length} routes).`);
}
