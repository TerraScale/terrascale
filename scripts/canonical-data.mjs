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
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const origin = process.env.TERRASCALE_CANONICAL_ORIGIN ?? "https://terrascale.tech";
const files = ["pricing.json", "regions.json"];
const mode = ["--sync", "--check", "--build"].find((flag) => process.argv.includes(flag)) ?? "--check";

const vendoredPath = (file) => join(root, "data", "canonical", file);

async function fetchCanonical(file) {
  const url = `${origin}/${file}`;
  const response = await fetch(url, { headers: { accept: "application/json" }, signal: AbortSignal.timeout(15_000) });
  if (!response.ok) throw new Error(`GET ${url} returned HTTP ${response.status}`);
  const text = await response.text();
  JSON.parse(text);
  return text;
}

const isNetworkError = (error) =>
  error?.name === "TimeoutError" || error?.name === "AbortError" || error?.cause?.code !== undefined || error instanceof TypeError;

let failed = false;
for (const file of files) {
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
