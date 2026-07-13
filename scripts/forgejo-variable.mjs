#!/usr/bin/env node

const [name, value] = process.argv.slice(2);

if (!name || value === undefined) {
  console.error("Usage: forgejo-variable.mjs <name> <value>");
  process.exit(2);
}

const token = process.env.REGISTRY_TOKEN ?? process.env.FORGEJO_TOKEN ?? process.env.GITHUB_TOKEN;
const api = process.env.FORGEJO_API_URL ?? process.env.GITHUB_API_URL ?? "https://git.terrascale.tech/api/v1";
const repository = process.env.FORGEJO_REPOSITORY ?? process.env.GITHUB_REPOSITORY;

if (!token) {
  console.error("REGISTRY_TOKEN, FORGEJO_TOKEN, or GITHUB_TOKEN is required.");
  process.exit(2);
}

if (!repository) {
  console.error("FORGEJO_REPOSITORY or GITHUB_REPOSITORY is required.");
  process.exit(2);
}

const url = `${api}/repos/${repository}/actions/variables/${encodeURIComponent(name)}`;
const headers = {
  Authorization: `token ${token}`,
  "Content-Type": "application/json",
};
const body = JSON.stringify({ value });

let response = await fetch(url, { method: "PUT", headers, body });
if (response.status === 404) {
  response = await fetch(url, { method: "POST", headers, body });
}

if (!response.ok) {
  console.error(await response.text());
  console.error(`Could not set Forgejo variable ${name}. HTTP ${response.status}.`);
  process.exit(1);
}
