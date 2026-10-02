#!/usr/bin/env node
// Generates or checks sdk-v1-route-manifest.json from the route table the v1
// client builds its URLs from (src/v1/routes.js) and the error codes it knows
// (src/v1/errors.js).
//
// The v1 producer publishes its contract as the frozen terrabase documents
// docs/api-v1.md, docs/query-v1.md and docs/shapes-v1.md. The manifest pins
// those documents by SHA-256 at one terrabase revision. --check always
// verifies that the manifest matches the client. It also verifies the contract
// when one is supplied:
//
// - TERRABASE_V1_CONTRACT_DIR: absolute path of a terrabase docs directory, or
// - TERRABASE_REPO: absolute path of a terrabase git checkout; the documents
//   are read with `git show <pinned revision>:docs/<name>`.
//
// With either, --check verifies the pinned digests, that every route's actual
// method and path are declared in its contract document (scripts/v1-contract.js),
// that each route's contractText notes appear there, and that the client's
// error codes equal the api-v1.md error table.
//
// TERRABASE_V1_OPENAPI: absolute path of the producer's v1 OpenAPI 3.1
// document (crates/tb-server/assets/client-openapi.json). When set, --check
// also requires every route's method and path to be declared there. The
// producer has not published a v1 OpenAPI yet; once it does, this becomes the
// primary parity check like sdk-route-manifest.json.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { V1ErrorCode } from "../src/v1/errors.js";
import { v1Routes } from "../src/v1/routes.js";
import { verifyRouteDeclarations } from "./v1-contract.js";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const target = path.join(root, "sdk-v1-route-manifest.json");

const contractSource = {
  producer: "terrabase",
  branch: "feat/v1-contracts",
  revision: "7bc74f9d224167ff2dc6b815965305feca210c00",
  status: "frozen contract documents; producer OpenAPI and runtime qualification pending",
  documents: {
    "api-v1.md": "04f494248d8f3221675b26e997f8765b957e2b3f1bb19e6fa091f97b499420d5",
    "query-v1.md": "c5b0bdf07bd37b62c954526bc17af7cfbf22ed30f5545c5e23da68d2cb3a3e39",
    "shapes-v1.md": "be919ff760e5793eec5b9e249c4b1e18d0b306aecf418d1a3cd6c48b0b244106",
  },
};

// Codes the client synthesizes itself; they are not in the contract table.
const clientOnlyCodes = new Set(["not_modified"]);
const errorCodes = Object.values(V1ErrorCode).filter((code) => !clientOnlyCodes.has(code)).sort();

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
  errorCodes,
};
const output = `${JSON.stringify(manifest, null, 2)}\n`;

/**
 * @returns {Promise<Record<string, Buffer> | undefined>}
 */
async function contractDocuments() {
  const directory = process.env.TERRABASE_V1_CONTRACT_DIR;
  const repository = process.env.TERRABASE_REPO;
  if (directory !== undefined && directory !== "") {
    if (!path.isAbsolute(directory)) throw new Error("TERRABASE_V1_CONTRACT_DIR must be an absolute path.");
    /** @type {Record<string, Buffer>} */
    const documents = {};
    for (const name of Object.keys(contractSource.documents)) documents[name] = await readFile(path.join(directory, name));
    return documents;
  }
  if (repository !== undefined && repository !== "") {
    if (!path.isAbsolute(repository)) throw new Error("TERRABASE_REPO must be an absolute path.");
    /** @type {Record<string, Buffer>} */
    const documents = {};
    for (const name of Object.keys(contractSource.documents))
      documents[name] = execFileSync("git", ["-C", repository, "show", `${contractSource.revision}:docs/${name}`], { maxBuffer: 16 * 1024 * 1024 });
    return documents;
  }
  return undefined;
}

/**
 * The codes in api-v1.md's "## Errors" table.
 * @param {string} text
 * @returns {string[]}
 */
function contractErrorCodes(text) {
  const section = text.split(/^## Errors$/mu)[1]?.split(/^## /mu)[0];
  if (section === undefined) throw new Error("api-v1.md has no Errors section.");
  /** @type {Set<string>} */
  const codes = new Set();
  for (const line of section.split("\n")) {
    const row = /^\| (\d{3}) \| (.*) \|$/u.exec(line);
    if (row === null) continue;
    // Only the codes themselves: backticked tokens before any parenthesized note.
    const cell = (row[2] ?? "").replace(/\([^)]*\)/gu, "");
    for (const match of cell.matchAll(/`([a-z_]+)`/gu)) codes.add(/** @type {string} */ (match[1]));
  }
  return [...codes].sort();
}

/**
 * @param {string} file
 * @returns {Promise<number>}
 */
async function checkOpenApi(file) {
  if (!path.isAbsolute(file)) throw new Error("TERRABASE_V1_OPENAPI must be an absolute path.");
  const document = JSON.parse(await readFile(file, "utf8"));
  const paths = document?.paths;
  if (typeof paths !== "object" || paths === null) throw new Error("The OpenAPI document has no paths.");
  for (const route of v1Routes) {
    const item = paths[route.pathTemplate];
    if (item === undefined) throw new Error(`OpenAPI does not declare ${route.pathTemplate} (${route.operation}).`);
    if (item[route.method.toLowerCase()] === undefined)
      throw new Error(`OpenAPI does not declare ${route.method} ${route.pathTemplate} (${route.operation}).`);
  }
  return v1Routes.length;
}

if (process.argv.includes("--check")) {
  if ((await readFile(target, "utf8")) !== output)
    throw new Error("sdk-v1-route-manifest.json differs from src/v1/routes.js or src/v1/errors.js; run npm run generate:v1-route-manifest.");
  const documents = await contractDocuments();
  if (documents === undefined) {
    console.log(`v1 route manifest matches the client (${v1Routes.length} routes); contract documents not supplied (TERRABASE_REPO or TERRABASE_V1_CONTRACT_DIR), digest parity skipped.`);
  } else {
    /** @type {Record<string, string>} */
    const texts = {};
    for (const [name, digest] of Object.entries(contractSource.documents)) {
      const bytes = /** @type {Buffer} */ (documents[name]);
      if (createHash("sha256").update(bytes).digest("hex") !== digest)
        throw new Error(`${name} differs from the pinned v1 contract digest.`);
      texts[name] = bytes.toString("utf8");
    }
    verifyRouteDeclarations(v1Routes, texts);
    for (const route of v1Routes)
      for (const literal of route.contractText)
        if (!(texts[route.contract] ?? "").includes(literal))
          throw new Error(`${route.operation} is not declared in ${route.contract} as ${literal}.`);
    const contractCodes = contractErrorCodes(texts["api-v1.md"] ?? "");
    if (JSON.stringify(contractCodes) !== JSON.stringify(errorCodes))
      throw new Error(`Client error codes differ from the api-v1.md error table.\n contract: ${contractCodes.join(", ")}\n client:   ${errorCodes.join(", ")}`);
    console.log(`v1 route manifest matches the client and the contract documents at ${contractSource.revision.slice(0, 8)} (${v1Routes.length} routes, ${errorCodes.length} error codes).`);
  }
  const openapi = process.env.TERRABASE_V1_OPENAPI;
  if (openapi === undefined || openapi === "") console.log("No v1 OpenAPI supplied (TERRABASE_V1_OPENAPI); OpenAPI parity skipped.");
  else console.log(`v1 OpenAPI declares every client route (${await checkOpenApi(openapi)} routes).`);
} else {
  await writeFile(target, output);
  console.log(`Wrote sdk-v1-route-manifest.json (${v1Routes.length} routes, ${errorCodes.length} error codes).`);
}
