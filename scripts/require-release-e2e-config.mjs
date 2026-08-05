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

if (missing.length > 0) {
  throw new Error(`Release E2E configuration is missing: ${missing.join(", ")}.`);
}

console.log("Release E2E configuration is present.");
