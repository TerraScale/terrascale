#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";

const [method, url, outputPath, bodyPath] = process.argv.slice(2);

if (!method || !url || !outputPath) {
  console.error("Usage: forgejo-api.mjs <method> <url> <output-path> [body-path]");
  process.exit(2);
}

const token = process.env.REGISTRY_TOKEN ?? process.env.FORGEJO_TOKEN ?? process.env.GITHUB_TOKEN;
const headers = {};
const options = { method, headers };

if (token) {
  headers.Authorization = `token ${token}`;
}

if (bodyPath) {
  headers["Content-Type"] = "application/json";
  options.body = await readFile(bodyPath);
}

const response = await fetch(url, options);
const text = await response.text();
await writeFile(outputPath, text);
process.stdout.write(String(response.status));
