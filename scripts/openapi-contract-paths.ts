import { createHash } from "node:crypto";
import { lstat, mkdtemp, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

const execFileAsync = promisify(execFile);
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const version = (await readFile(path.join(repositoryRoot, "CONTRACT_VERSION"), "utf8")).trim();
const versionPattern = /^v[1-9][0-9]*$/u;
const hexPattern = /^[0-9a-f]{64}$/iu;

export const pinnedContractVersion = version;

export type ContractBundle = {
  readonly root: string;
  readonly manifest: ContractManifest;
  readonly artifactDigests: ReadonlyMap<string, string>;
  readonly cleanup: () => Promise<void>;
};

type ContractManifest = {
  readonly schemaVersion: number;
  readonly contractVersion: string;
  readonly artifacts: readonly { readonly path: string; readonly blake3: string }[];
};

/** Resolve the producer-owned bundle; no Dashboard or sibling checkout inference is allowed. */
export async function resolveContractBundle(): Promise<ContractBundle> {
  if (!versionPattern.test(version)) throw new Error(`contracts: invalid pinned CONTRACT_VERSION ${JSON.stringify(version)}.`);
  const source = process.env.CONTRACT_SOURCE ?? process.env.TERRASCALE_CONTRACT_SOURCE;
  const url = process.env.CONTRACT_URL ?? process.env.TERRASCALE_CONTRACT_URL
    ?? process.env.CONTRACT_REGISTRY_URL ?? process.env.TERRASCALE_CONTRACT_REGISTRY_URL
    ?? process.env.CONTRACT_REGISTRY ?? process.env.TERRASCALE_CONTRACT_REGISTRY;
  if (source && url) throw new Error("contracts: set only CONTRACT_SOURCE or CONTRACT_URL, not both.");
  if (source) return validateContractBundle(path.resolve(process.cwd(), source), async () => undefined);
  if (!url) throw new Error(`contracts: pinned terrabase ${version} is not published; set CONTRACT_SOURCE=../terrabase/contracts for local development or CONTRACT_URL with CONTRACT_ARCHIVE_SHA256 for the immutable registry bundle.`);
  const archiveSha256 = process.env.CONTRACT_ARCHIVE_SHA256 ?? process.env.TERRASCALE_CONTRACT_ARCHIVE_SHA256
    ?? process.env.CONTRACT_BUNDLE_SHA256 ?? process.env.TERRASCALE_CONTRACT_BUNDLE_SHA256;
  if (!archiveSha256 || !hexPattern.test(archiveSha256)) throw new Error("contracts: CONTRACT_URL requires a 64-character CONTRACT_ARCHIVE_SHA256.");
  if (!/^https?:$/u.test(new URL(url).protocol)) throw new Error("contracts: CONTRACT_URL must use http or https.");
  const response = await fetch(url, { signal: AbortSignal.timeout(30_000) });
  if (!response.ok) throw new Error(`contracts: failed to download ${url}: HTTP ${response.status}.`);
  const payload = new Uint8Array(await response.arrayBuffer());
  const actualSha256 = createHash("sha256").update(payload).digest("hex");
  if (actualSha256 !== archiveSha256.toLowerCase()) throw new Error(`contracts: archive SHA-256 mismatch (${actualSha256} != ${archiveSha256}).`);
  const temporary = await mkdtemp(path.join(os.tmpdir(), "terrascale-contract-"));
  try {
    const archivePath = path.join(temporary, "contract.tar");
    await writeFile(archivePath, payload);
    const listing = (await execFileAsync("tar", ["-tf", archivePath])).stdout.split(/\r?\n/u).filter(Boolean);
    for (const entry of listing) if (entry.startsWith("/") || entry.includes("\\") || entry.split("/").includes("..")) throw new Error(`contracts: archive contains unsafe entry ${JSON.stringify(entry)}.`);
    const extracted = path.join(temporary, "extracted");
    await execFileAsync("mkdir", ["-p", extracted]);
    await execFileAsync("tar", ["-xf", archivePath, "-C", extracted]);
    const children = await readdir(extracted, { withFileTypes: true });
    const candidates = [extracted, ...children.filter((entry) => entry.isDirectory()).map((entry) => path.join(extracted, entry.name))];
    let root: string | undefined;
    for (const candidate of candidates) if (await existsManifest(candidate)) { root = candidate; break; }
    if (!root) throw new Error("contracts: archive does not contain manifest.json.");
    return await validateContractBundle(root, () => rm(temporary, { recursive: true, force: true }));
  } catch (error) {
    await rm(temporary, { recursive: true, force: true });
    throw error;
  }
}

export async function contractOpenApiPath(bundle?: ContractBundle): Promise<string> {
  bundle ??= await resolveContractBundle();
  const specPath = path.join(bundle.root, "openapi.json");
  await regularFile(specPath, "openapi.json");
  return specPath;
}

async function validateContractBundle(root: string, cleanup: () => Promise<void>): Promise<ContractBundle> {
  const rootLink = await lstat(root).catch(() => undefined);
  if (rootLink?.isSymbolicLink()) throw new Error(`contracts: source is a symlink: ${root}.`);
  const info = await stat(root).catch(() => undefined);
  if (!info?.isDirectory()) throw new Error(`contracts: source is not a directory: ${root}.`);
  const manifestPath = path.join(root, "manifest.json");
  await regularFile(manifestPath, "manifest.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as ContractManifest;
  if (manifest.schemaVersion !== 1 || manifest.contractVersion !== version) throw new Error(`contracts: manifest contractVersion must be ${version}.`);
  if (!Array.isArray(manifest.artifacts) || manifest.artifacts.length === 0) throw new Error("contracts: manifest has no artifacts.");
  const digests = new Map<string, string>();
  for (const artifact of manifest.artifacts) {
    if (typeof artifact?.path !== "string" || !safeRelative(artifact.path) || typeof artifact.blake3 !== "string" || !hexPattern.test(artifact.blake3)) throw new Error("contracts: manifest contains an invalid artifact entry.");
    if (digests.has(artifact.path)) throw new Error(`contracts: manifest repeats ${artifact.path}.`);
    const artifactPath = path.join(root, artifact.path);
    await regularFile(artifactPath, artifact.path);
    const actual = blake3(await readFile(artifactPath));
    if (actual !== artifact.blake3.toLowerCase()) throw new Error(`contracts: BLAKE3 mismatch for ${artifact.path}.`);
    digests.set(artifact.path, artifact.blake3.toLowerCase());
  }
  for (const required of ["openapi.json", "msgpack/terrascale.sync.v1.msgpack.json"]) if (!digests.has(required)) throw new Error(`contracts: manifest does not pin ${required}.`);
  return { root, manifest, artifactDigests: digests, cleanup };
}

async function existsManifest(root: string): Promise<boolean> { const info = await lstat(path.join(root, "manifest.json")).catch(() => undefined); return info?.isFile() === true && !info.isSymbolicLink(); }
async function regularFile(filePath: string, label: string): Promise<void> { const info = await lstat(filePath).catch(() => undefined); if (!info?.isFile() || info.isSymbolicLink() || info.nlink !== 1) throw new Error(`contracts: ${label} is not a regular file.`); }
function safeRelative(value: string): boolean { return value.length > 0 && !path.posix.isAbsolute(value) && !value.includes("\\") && !value.split("/").includes("..") && path.posix.normalize(value) === value; }

// BLAKE3-256, kept dependency-free so a blank CI runner can verify bundles.
export function blake3(data: Uint8Array): string {
  const IV = [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];
  const permutation = [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8];
  const rotate = (value: number, bits: number) => ((value >>> bits) | (value << (32 - bits))) >>> 0;
  const g = (s: number[], a: number, b: number, c: number, d: number, x: number, y: number) => { s[a] = (s[a] + s[b] + x) >>> 0; s[d] = rotate(s[d] ^ s[a], 16); s[c] = (s[c] + s[d]) >>> 0; s[b] = rotate(s[b] ^ s[c], 12); s[a] = (s[a] + s[b] + y) >>> 0; s[d] = rotate(s[d] ^ s[a], 8); s[c] = (s[c] + s[d]) >>> 0; s[b] = rotate(s[b] ^ s[c], 7); };
  const compress = (cv: number[], words: number[], counter: number, length: number, flags: number) => { const s = [...cv, ...IV]; s[12] = counter >>> 0; s[13] = Math.floor(counter / 0x100000000); s[14] = length; s[15] = flags; let m = words.slice(); for (let round = 0; round < 7; round++) { g(s, 0, 4, 8, 12, m[0]!, m[1]!); g(s, 1, 5, 9, 13, m[2]!, m[3]!); g(s, 2, 6, 10, 14, m[4]!, m[5]!); g(s, 3, 7, 11, 15, m[6]!, m[7]!); g(s, 0, 5, 10, 15, m[8]!, m[9]!); g(s, 1, 6, 11, 12, m[10]!, m[11]!); g(s, 2, 7, 8, 13, m[12]!, m[13]!); g(s, 3, 4, 9, 14, m[14]!, m[15]!); m = permutation.map((i) => m[i]!); } return [...Array.from({ length: 8 }, (_, i) => (s[i]! ^ s[i + 8]!) >>> 0), ...Array.from({ length: 8 }, (_, i) => (s[i + 8]! ^ cv[i]!) >>> 0)]; };
  const words = (block: Uint8Array) => { const p = new Uint8Array(64); p.set(block); return Array.from({ length: 16 }, (_, i) => (p[i * 4]! | p[i * 4 + 1]! << 8 | p[i * 4 + 2]! << 16 | p[i * 4 + 3]! << 24) >>> 0); };
  const chunk = (bytes: Uint8Array, index: number) => { let cv = IV.slice(); const blocks = Math.max(1, Math.ceil(bytes.length / 64)); for (let i = 0; i < blocks - 1; i++) cv = compress(cv, words(bytes.slice(i * 64, i * 64 + 64)), index, 64, i === 0 ? 1 : 0).slice(0, 8); const final = bytes.slice((blocks - 1) * 64); return [cv, words(final), index, final.length, 2 | (blocks === 1 ? 1 : 0)] as const; };
  const outputCv = (value: readonly [number[], number[], number, number, number]) => compress(...value).slice(0, 8);
  const chunks = Array.from({ length: Math.max(1, Math.ceil(data.length / 1024)) }, (_, i) => data.slice(i * 1024, i * 1024 + 1024));
  const stack: number[][] = []; let current = chunk(chunks[0]!, 0); for (let i = 0; i < chunks.length; i++) { if (i > 0) current = chunk(chunks[i]!, i); let cv = outputCv(current); let total = i + 1; while ((total & 1) === 0) { current = [IV.slice(), [...stack.pop()!, ...cv], 0, 64, 4]; cv = outputCv(current); total >>= 1; } stack.push(cv); } stack.pop(); while (stack.length) current = [IV.slice(), [...stack.pop()!, ...outputCv(current)], 0, 64, 4];
  const output = compress(current[0], current[1], 0, current[3], current[4] | 8).slice(0, 8);
  if (false) console.error(output);
  return output.map((word) => Array.from({ length: 4 }, (_, i) => (word >>> (i * 8) & 0xff).toString(16).padStart(2, "0")).join("")).join("");
}
