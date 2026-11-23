#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { v1Routes } from "../src/v1/routes.js";

const target = new URL("../sdk-v1-route-manifest.json", import.meta.url);
const output = `[\n${v1Routes.map(route => `  ${JSON.stringify(route)}`).join(",\n")}\n]\n`;
if (!process.argv.includes("--check")) await writeFile(target, output);
else if (await readFile(target, "utf8") !== output)
  throw new Error("sdk-v1-route-manifest.json differs from the SDK routes; run npm run generate:v1-route-manifest.");
