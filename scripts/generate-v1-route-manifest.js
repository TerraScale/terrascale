#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { generateV1Manifest } from "./terrabase-contract.mjs";

const target = new URL("../sdk-v1-route-manifest.json", import.meta.url);
const output = `${JSON.stringify(await generateV1Manifest(), null, 2)}\n`;
if (process.argv.includes("--check")) {
  if (await readFile(target, "utf8") !== output)
    throw new Error("sdk-v1-route-manifest.json differs from the C# producer and client; run npm run generate:v1-route-manifest.");
  console.log("Route manifest matches historical C# OpenAPI, current pinned handler source and SDK routes.");
} else {
  await writeFile(target, output);
  console.log("Generated SDK route manifest from historical OpenAPI and current pinned C# handlers.");
}
