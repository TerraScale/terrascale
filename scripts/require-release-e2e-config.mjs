#!/usr/bin/env node

const required = [
  "TERRASCALE_API_BASE_URL",
  "TERRASCALE_AUTH_API_BASE_URL",
  "TERRASCALE_E2E_ACCESS_TOKEN",
  "TERRASCALE_E2E_PROJECT_ID",
  "TERRASCALE_E2E_ORGANIZATION_ID",
  "TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID",
];
const missing = required.filter((name) => (process.env[name] ?? "").trim().length === 0);
const invalidUrls = ["TERRASCALE_API_BASE_URL", "TERRASCALE_AUTH_API_BASE_URL"]
  .filter((name) => !isHttpUrl(process.env[name]));
const invalidUlids = [
  "TERRASCALE_E2E_PROJECT_ID",
  "TERRASCALE_E2E_ORGANIZATION_ID",
  "TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID",
].filter((name) => !/^[0-9A-HJKMNP-TV-Z]{26}$/iu.test(process.env[name] ?? ""));

if (missing.length > 0) {
  throw new Error(`Release E2E configuration is missing: ${missing.join(", ")}.`);
}

if (invalidUrls.length > 0 || invalidUlids.length > 0) {
  throw new Error(`Release E2E configuration is invalid: ${[...invalidUrls, ...invalidUlids].join(", ")}.`);
}

console.log("Release E2E configuration is present.");

function isHttpUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}
