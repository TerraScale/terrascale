// Snippet runner: executes every docs/snippets/*.ts against the stub sandbox
// (default) or a real sandbox branch (--sandbox=remote).
// Remote mode needs TERRASCALE_SANDBOX_URL + TERRASCALE_API_KEY and the pinned
// contract (P-1); without them it fails closed with the fix.
import { readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { pathToFileURL, fileURLToPath } from "node:url";
import { createStubSandbox } from "./stub-sandbox.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const snippetsDir = join(root, "docs", "snippets");
const remote = process.argv.includes("--sandbox=remote");

function createRemoteSandbox() {
  const base = process.env.TERRASCALE_SANDBOX_URL;
  const key = process.env.TERRASCALE_API_KEY;
  if (!base || !key) {
    throw new Error(
      "remote sandbox needs TERRASCALE_SANDBOX_URL + TERRASCALE_API_KEY " +
        "(a cloud dev branch pre-filled with the reader's key; D-56). " +
        "Run without --sandbox=remote for the stub.",
    );
  }
  // Fail-closed remote client: same interface as the stub, backed by fetch.
  // Full REST surface (auth, SQL, view attach) follows the pinned contract (P-1).
  const headers = { "content-type": "application/json", authorization: `Bearer ${key}` };
  const mem = createStubSandbox();
  return {
    mode: "remote",
    async put(collection, doc) {
      const res = await fetch(`${base}/api/v1/collections/${collection}/docs`, {
        method: "POST",
        headers,
        body: JSON.stringify(doc),
      });
      if (!res.ok) throw new Error(`remote put failed: ${res.status}`);
      return res.json();
    },
    async get(collection, id) {
      const res = await fetch(`${base}/api/v1/collections/${collection}/docs/${id}`, {
        headers,
      });
      if (res.status === 404) return null;
      if (!res.ok) throw new Error(`remote get failed: ${res.status}`);
      return res.json();
    },
    remove: mem.remove,
    query: mem.query,
    subscribe: mem.subscribe,
    createTenant: mem.createTenant,
  };
}

export async function runAll() {
  const files = readdirSync(snippetsDir).filter((f) => f.endsWith(".ts")).sort();
  if (files.length === 0) throw new Error("no snippets found in docs/snippets/");
  const sandbox = remote ? createRemoteSandbox() : createStubSandbox();
  const results = [];
  for (const f of files) {
    const mod = await import(pathToFileURL(join(snippetsDir, f)).href);
    if (typeof mod.run !== "function") throw new Error(`${f} must export run(sandbox)`);
    await mod.run(sandbox);
    results.push({ file: f, ok: true });
    console.log(`ok - ${f} (${sandbox.mode})`);
  }
  return results;
}

// Direct run: `node scripts/run-snippets.mjs [--sandbox=remote]`
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const results = await runAll();
    console.log(`${results.length} snippet(s) passed.`);
  } catch (err) {
    console.error(`FAIL: ${err.message}`);
    process.exit(1);
  }
}
