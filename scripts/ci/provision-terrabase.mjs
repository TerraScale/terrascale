// Acquire only the exact C# release-job binary, never a checkout build or a fake server.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { chmod, mkdtemp, readFile, rename, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { isJsonObject } from "../../src/v1/json.js";

const pin = JSON.parse(await readFile(new URL("./terrabase-release.json", import.meta.url), "utf8"));

/** @param {{ registryOrigin: string, producer: string }} releasePin */
export function releaseApiUrl(releasePin) {
  const origin = new URL(releasePin.registryOrigin);
  assert.ok(origin.protocol === "https:" && [origin.origin, `${origin.origin}/`].includes(releasePin.registryOrigin), "Release registry must be an absolute HTTPS origin without path, query, fragment or credentials.");
  return `${origin.origin}/api/v1/repos/${releasePin.producer}`;
}

/** @param {string | null} location @param {string} base */
export function releaseRedirectUrl(location, base) {
  assert.ok(location !== null && location.trim() !== "", "Release binary redirect is missing a nonempty Location header.");
  const url = new URL(location, base);
  assert.equal(url.protocol, "https:");
  return url.href;
}

/** @param {unknown} value @param {{ revision: string, version: string, sha256: string, asset: string, registryOrigin: string, producer: string }} releasePin */
export function releaseAsset(value, releasePin) {
  assert.ok(isJsonObject(value), "Release metadata must be a nonnull object.");
  assert.equal(typeof value.draft, "boolean", "Release metadata has no draft status.");
  assert.equal(value.target_commitish, releasePin.revision, "Release source revision changed.");
  assert.ok(typeof value.body === "string", "Release notes must be a string.");
  assert.match(value.body, new RegExp(`terrabase ${releasePin.version.replaceAll(".", "\\.")}`));
  assert.ok(value.body.includes(releasePin.sha256), "Release notes do not confirm the pinned SHA-256.");
  assert.ok(Array.isArray(value.assets), "Release assets must be an array.");
  const asset = value.assets.find(item => isJsonObject(item) && item.name === releasePin.asset);
  assert.ok(isJsonObject(asset), "Release binary is missing.");
  assert.ok(typeof asset.browser_download_url === "string" && asset.browser_download_url.trim() !== "", "Release binary download URL must be a nonempty string.");
  const url = new URL(asset.browser_download_url);
  assert.ok(asset.browser_download_url.startsWith("https://") && url.origin === new URL(releaseApiUrl(releasePin)).origin && !url.username && !url.password, "Release binary download URL must use the HTTPS registry origin without credentials.");
  assert.ok(typeof asset.size === "number" && Number.isSafeInteger(asset.size) && asset.size > 0, "Release binary size must be a positive safe integer.");
  return { browser_download_url: url.href, size: asset.size };
}

/** Downloads the exact official release-job asset and returns its verified absolute path. */
export async function provisionTerraBase() {
  const api = releaseApiUrl(pin);
  const token = process.env.FORGEJO_TOKEN;
  /** @type {Record<string, string>} */
  const headers = token ? { Authorization: `token ${token}` } : {};
  const releaseResponse = await fetch(`${api}/releases/tags/${pin.release}`, { headers, redirect: "error", signal: AbortSignal.timeout(30_000) });
  assert.equal(releaseResponse.status, 200, `Cannot read release ${pin.release}; private repository downloads require FORGEJO_TOKEN.`);
  const release = await releaseResponse.json();
  const asset = releaseAsset(release, pin);
  console.log(`Release ${pin.release}: draft=${release.draft}, prerelease=${release.prerelease}`);
  // Resolve the authenticated same-origin download without forwarding the token to a redirect host.
  let url = asset.browser_download_url;
  let response = await fetch(url, { headers, redirect: "manual", signal: AbortSignal.timeout(120_000) });
  if ([301, 302, 303, 307, 308].includes(response.status)) {
    url = releaseRedirectUrl(response.headers.get("location"), url);
    response = await fetch(url, { redirect: "error", signal: AbortSignal.timeout(120_000) });
  }
  assert.equal(response.status, 200, "Release binary download failed.");
  const bytes = Buffer.from(await response.arrayBuffer());
  assert.equal(bytes.length, asset.size, "Release binary size changed.");
  assert.equal(createHash("sha256").update(bytes).digest("hex"), pin.sha256, "Release binary SHA-256 mismatch.");
  const directory = await mkdtemp(path.join(process.env.RUNNER_TEMP ?? tmpdir(), "ts498-terrabase-"));
  const binary = path.join(directory, pin.asset);
  await writeFile(`${binary}.download`, bytes, { mode: 0o700 });
  await rename(`${binary}.download`, binary);
  await chmod(binary, 0o700);
  if (process.env.GITHUB_ENV) await writeFile(process.env.GITHUB_ENV, `TERRABASE_BINARY=${binary}\n`, { flag: "a" });
  console.log(`TERRABASE_BINARY=${binary}`);
  console.log(`Pinned C# TerraBase ${pin.version} ${pin.revision} SHA-256 ${pin.sha256}`);
  return binary;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  await provisionTerraBase();
