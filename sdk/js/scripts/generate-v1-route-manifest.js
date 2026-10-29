#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { generateV1Manifest } from "./terrabase-contract.mjs";
import { fileStorageRoutes } from "../src/v1/file-storage-routes.js";

const filePin = JSON.parse(await readFile(new URL("../contracts/file-storage/pin.json", import.meta.url), "utf8"));
for (const [name, manifest] of [
  ["sdk-v1-route-manifest.json", await generateV1Manifest()],
  ["sdk-file-storage-route-manifest.json", { producerRevision: filePin.revision, routes: fileStorageRoutes }],
]) {
  const target = new URL(`../${name}`, import.meta.url);
  const output = `${JSON.stringify(manifest, null, 2)}\n`;
  if (process.argv.includes("--check")) {
    if (await readFile(target, "utf8") !== output)
      throw new Error(`${name} differs from the producer and client; run npm run generate:v1-route-manifest.`);
  } else await writeFile(target, output);
}
console.log("Route manifests match the pinned C# producer and SDK routes.");
