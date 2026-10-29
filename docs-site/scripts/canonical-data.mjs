// Canonical pricing and region catalogs are published by the landing site at
// https://terrascale.tech/pricing.json and https://terrascale.tech/regions.json.
// This repo vendors byte-identical copies in data/canonical/ so offline builds
// work; generated pricing pages read only the vendored copies.
//
// Usage: node scripts/canonical-data.mjs [--sync | --check | --build]
//   --sync   fetch both catalogs and overwrite the vendored copies.
//   --check  fetch both catalogs and fail when they cannot be fetched or the
//            vendored copies have drifted (the parity check run by `npm test`).
//   --build  like --check, but an unreachable network only warns so offline
//            builds proceed from the vendored copies. Drift still fails.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import timers from "node:timers/promises";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const origin = "https://terrascale.tech";
const files = ["pricing.json", "regions.json"];
const mode = ["--sync", "--check", "--build"].find((flag) => process.argv.includes(flag)) ?? "--check";

const vendoredPath = (file) => join(root, "data", "canonical", file);

export function validateCanonical(file, text) {
  const catalog = JSON.parse(text);
  if (file === "regions.json") {
    const expected = [
      ["gru", "São Paulo", "sa-saopaulo-1"],
      ["sjc", "San Francisco Bay Area", "us-sanjose-1"],
      ["fra", "Frankfurt", "eu-frankfurt-1"],
    ];
    if (!Array.isArray(catalog.regions) || catalog.regions.length !== expected.length ||
        expected.some(([id, name, providerRegion]) => !catalog.regions.some((region) =>
          region?.id === id && region.name === name && region.provider === "oci" && region.providerRegion === providerRegion))) {
      throw new Error("regions.json must list the three OCI launch regions with their canonical names and OCI region identifiers.");
    }
  }
}

const isNetworkError = (error) =>
  error?.name === "TimeoutError" || error?.name === "AbortError" ||
  ["ECONNRESET", "ECONNREFUSED", "ETIMEDOUT", "ENOTFOUND", "EAI_AGAIN", "ENETUNREACH", "EHOSTUNREACH",
    "UND_ERR_CONNECT_TIMEOUT", "UND_ERR_HEADERS_TIMEOUT", "UND_ERR_BODY_TIMEOUT", "UND_ERR_SOCKET"].includes(error?.cause?.code ?? error?.code);

export async function fetchCanonical(file) {
  const url = `${origin}/${file}`;
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const response = await fetch(url, { headers: { accept: "application/json" }, signal: AbortSignal.timeout(15_000) });
      if (!response.ok) throw new Error(`GET ${url} returned HTTP ${response.status}`);
      const text = await response.text();
      validateCanonical(file, text);
      return text;
    } catch (error) {
      if (attempt === 1 || !isNetworkError(error)) throw error;
      console.warn(`canonical: ${url}: transport failed; retrying in 5 seconds.`);
      await timers.setTimeout(5_000);
    }
  }
}

export async function main() {
  let failed = false;
  for (const file of files) {
    try {
      if (mode !== "--sync") validateCanonical(file, readFileSync(vendoredPath(file), "utf8"));
    } catch (error) {
      console.error(`FAIL: data/canonical/${file}: ${error.message}`);
      failed = true;
      continue;
    }
    let published;
    try {
      published = await fetchCanonical(file);
    } catch (error) {
      if (mode === "--build" && isNetworkError(error)) {
        console.warn(`canonical: ${origin}/${file} is unreachable (${error.cause?.code ?? error.message}); building from the vendored data/canonical/${file}.`);
        continue;
      }
      console.error(`FAIL: could not fetch ${origin}/${file}: ${error.message}`);
      failed = true;
      continue;
    }
    if (mode === "--sync") {
      writeFileSync(vendoredPath(file), published);
      console.log(`canonical: wrote data/canonical/${file} from ${origin}/${file}`);
    } else if (published !== readFileSync(vendoredPath(file), "utf8")) {
      console.error(`FAIL: data/canonical/${file} drifted from ${origin}/${file}; run npm run canonical:sync and regenerate the pricing pages.`);
      failed = true;
    } else {
      console.log(`canonical: data/canonical/${file} matches ${origin}/${file}`);
    }
  }
  if (failed) process.exit(1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
