// Qualifies measurement-window and CLI guards, not TerraBase performance.
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { discoverRegions, measure } from "./parity.mjs";
const directory = await mkdtemp(join(tmpdir(), "parity-window-"));
const previous = process.env.TS_PEERS_FILE;
try {
  process.env.TS_PEERS_FILE = join(directory, "peers");
  await writeFile(process.env.TS_PEERS_FILE, "one gru 127.0.0.1\ntwo sjc 127.0.0.2\nthree fra 127.0.0.3\nfour gru 127.0.0.4\n");
  assert.deepEqual(discoverRegions(), ["fra", "gru", "sjc"]);
  await writeFile(process.env.TS_PEERS_FILE, "one gru\n");
  assert.throws(discoverRegions, /malformed/u);
  const link = join(directory, "runner.mjs");
  await symlink(fileURLToPath(new URL("./parity.mjs", import.meta.url)), link);
  await assert.rejects(promisify(execFile)(process.execPath, [link]), error => error.code === 2 && error.stderr.includes("usage:"));
  // Three slow operations: one starts in warmup, two in the window; the last drains after its end.
  const stats = await measure(() => new Promise(resolve => setTimeout(resolve, 2500)), {}, 1, AbortSignal.timeout(15_000));
  assert.equal(stats.started, 3);
  assert.equal(stats.ok, 2);
  assert.equal(stats.latencies.length, 2);
  assert.equal(stats.failed, 0);
  assert.equal(stats.stuck, false);
  assert.ok(stats.elapsed_s > 5);
  console.log(JSON.stringify({ ...stats, tail_retained: true, symlink_guard: true, peers_gate: true }));
} finally {
  if (previous === undefined) delete process.env.TS_PEERS_FILE; else process.env.TS_PEERS_FILE = previous;
  await rm(directory, { recursive: true, force: true });
}
