// Vocabulary tests for the DB-07 topology view (TerraBase/TerraBaseV2#19).
//
// The topology page must speak CONTEXT.md exactly: Region, Member, Failure
// Domain, Acknowledgement Level, Convergence, ownership epoch, Bucket Range,
// storage-sequence distance — and never the avoided synonyms (zone, node,
// replica, cluster, eventual consistency, caught up). The acknowledgement and
// convergence panels use the engine's public report types verbatim
// (src/replication/mod.rs), so the wire enums and the measurement definitions
// are extracted from that source and compared, not re-typed from memory.
//
// Run with: node --test tests/topology-vocabulary.test.mjs
// (integration into `vp run unit:test` is a one-line vite.config.ts addition
// owned by the integrator; the studio config file is shared across the
// parallel studio tickets.)

import assert from "node:assert/strict";
import test from "node:test";
import { readFile, readdir } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import {
  ACKNOWLEDGEMENT_LEVELS,
  CONVERGENCE_DEFINITION,
  LAG_DEFINITION,
  MEMBER_STATUSES,
} from "../app/topology/fixtures/topology-demo.mjs";

const studioRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(studioRoot, "..");
const replicationSource = await readFile(join(repoRoot, "src", "replication", "mod.rs"), "utf8");

/** Maps a Rust enum variant to the studio wire name (MajorityRegions -> majority-regions). */
function wireName(variant) {
  return variant.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
}

function enumVariants(source, name) {
  const match = source.match(new RegExp(`pub enum ${name} \\{([\\s\\S]*?)\\n\\}`));
  assert.ok(match !== null, `pub enum ${name} not found in src/replication/mod.rs`);
  return [...match[1].matchAll(/^ {4}([A-Z][A-Za-z0-9]*),$/gm)].map((found) => found[1]);
}

/** Extracts a `pub const NAME: &str = "..."` literal, joining Rust line continuations. */
function stringConst(source, name) {
  const match = source.match(new RegExp(`pub const ${name}: &str = ([\\s\\S]*?);\\n`));
  assert.ok(match !== null, `pub const ${name} not found in src/replication/mod.rs`);
  return match[1]
    .replace(/\\\s*\n\s*/g, "")
    .replace(/^"/, "")
    .replace(/"$/, "")
    .trim();
}

async function collectSources(dir) {
  const entries = await readdir(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await collectSources(path)));
    } else if (/\.(ts|tsx|mjs)$/.test(entry.name)) {
      files.push(path);
    }
  }
  return files;
}

const topologyFiles = await collectSources(join(studioRoot, "app", "topology"));
const topologySources = new Map(
  await Promise.all(topologyFiles.map(async (path) => [path, await readFile(path, "utf8")])),
);
const topologyCopy = [...topologySources.values()].join("\n");

test("the topology subtree exists with page modules", () => {
  const subtree = join(studioRoot, "app", "topology");
  const names = topologyFiles.map((path) => path.slice(subtree.length + 1));
  for (const expected of ["page.tsx", "loading.tsx", "error.tsx"]) {
    assert.ok(names.includes(expected), `app/topology/${expected} is missing`);
  }
});

test("AcknowledgementLevel wire names mirror the engine enum verbatim", () => {
  const variants = enumVariants(replicationSource, "AcknowledgementLevel");
  assert.deepEqual(variants.map(wireName), ACKNOWLEDGEMENT_LEVELS);
});

test("Member status wire names mirror the engine ReplicationStatus enum verbatim", () => {
  const variants = enumVariants(replicationSource, "ReplicationStatus");
  assert.deepEqual(variants.map(wireName), MEMBER_STATUSES);
});

test("the convergence and lag definitions are the engine's, verbatim", () => {
  assert.equal(stringConst(replicationSource, "CONVERGENCE_DEFINITION"), CONVERGENCE_DEFINITION);
  assert.equal(stringConst(replicationSource, "LAG_DEFINITION"), LAG_DEFINITION);
  assert.match(CONVERGENCE_DEFINITION, /in lane ticks of the lane clock/);
  assert.match(LAG_DEFINITION, /a sequence distance, not a latency claim/);
});

test("page copy uses the CONTEXT.md terms", () => {
  // CONTEXT.md terms the topology view is built from; each must appear in the
  // rendered vocabulary of the subtree.
  for (const term of [
    "Region",
    "Member",
    "Failure Domain",
    "Acknowledgement Level",
    "Convergence",
    "ownership epoch",
    "Bucket Range",
    "storage-sequence distance",
    "Shard",
  ]) {
    assert.ok(topologyCopy.includes(term), `topology copy is missing the term ${term}`);
  }
});

test("page copy avoids the CONTEXT.md avoided synonyms", () => {
  // Avoid lists from CONTEXT.md (Region: zone/site/cluster; Member:
  // node/replica; Convergence: eventual consistency/sync/caught up; Member
  // status copy therefore avoids them too).
  const banned = [
    /\bzones?\b/i,
    /\bsites?\b/i,
    /\bclusters?\b/i,
    /\bnodes?\b/i,
    /\breplicas?\b/i,
    /\bservers?\b/i,
    /eventual consistency/i,
    /caught up/i,
    /\bsync\b/i,
    /\bsynced\b/i,
  ];
  for (const [path, source] of topologySources) {
    // Import lines are module paths, not vocabulary (the shared studio lib
    // lives under src/lib/server); everything else must avoid the synonyms.
    const prose = source
      .split("\n")
      .filter((line) => !/^\s*(import|from|export .* from)\b/.test(line))
      .join("\n");
    for (const pattern of banned) {
      assert.ok(
        !pattern.test(prose),
        `${path} uses avoided synonym ${pattern} (CONTEXT.md avoid lists)`,
      );
    }
  }
});

test("the topology route is linked from the studio navigation", async () => {
  const nav = await readFile(
    join(studioRoot, "src", "components", "shell", "StudioNav.tsx"),
    "utf8",
  );
  assert.match(nav, /href: "\/topology"/);
  assert.match(nav, /label: "Topology"/);
  const planned = nav.match(/const PLANNED = \[([^\]]*)\]/)?.[1] ?? "";
  assert.ok(!planned.includes("Topology"), "Topology must leave the planned list");
});
