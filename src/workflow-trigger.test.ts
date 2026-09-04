import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("D-68 CI runs only on develop pushes", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /^  push:\r?\n    branches: \[develop\]$/mu);
  assert.doesNotMatch(workflow, /^\s+pull_request:/mu);
  assert.doesNotMatch(workflow, /^\s+(?:workflow_dispatch|schedule|workflow_call):/mu);
});

test("D-68 CI checks out the event revision and delegates to ts-deploy", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /uses:\s+https:\/\/data\.forgejo\.org\/actions\/checkout@[0-9a-f]{40}\b/u);
  assert.match(workflow, /ref:\s+\$\{\{\s*forgejo\.sha\s*\}\}/u);
  assert.match(workflow, /fetch-depth:\s*0\b/u);
  assert.match(workflow, /persist-credentials:\s*false\b/u);
  assert.match(workflow, /runs-on:\s*terrascale-builder\b/u);

  // Resolve the latest release to its immutable commit-derived tag before
  // downloading and verifying the selected architecture asset.
  assert.match(
    workflow,
    /\bcurl\b[^\r\n]*--proto '=https'[^\r\n]*--tlsv1\.2[^\r\n]*-fsSI[^\r\n]*https:\/\/git\.terrascale\.tech\/TerraScale\/ts-deploy\/releases\/latest/u,
  );
  assert.match(
    workflow,
    /releases\/tag\/v0\\\.0\\\.0-dev\\\.\(\[0-9a-f\]\{40\}\|\[0-9a-f\]\{64\}\)\$/u,
  );
  assert.match(workflow, /tag=\$\{location##\*\/\}/u);
  assert.match(
    workflow,
    /release="https:\/\/git\.terrascale\.tech\/TerraScale\/ts-deploy\/releases\/download\/\$\{tag\}"/u,
  );
  assert.match(workflow, /-o SHA256SUMS "\$\{release\}\/SHA256SUMS" -o ts-deploy "\$\{release\}\/\$\{asset\}"/u);
  assert.match(workflow, /grep -Fc "  \$\{asset\}" SHA256SUMS/u);
  assert.match(workflow, /awk -v asset="\$\{asset\}"[^\r\n]*sha256sum -c -/u);
  assert.doesNotMatch(workflow, /releases\/download\/latest(?:[\/'"`\s]|$)/u);

  const invocations = workflow.match(/\b(?:\.\/)?ts-deploy ci --repo ts-typescript\b/gu) ?? [];
  assert.equal(invocations.length, 1);
  assert.doesNotMatch(workflow, /^\s*(?:container|strategy|matrix):/mu);
  assert.doesNotMatch(
    workflow,
    /^(?:\s*-?\s*run:.*\b(?:npm|make contracts|git diff --exit-code)\b|.*\b(?:check:(?:openapi|forbidden-surface|route-manifest|public-contract-fixture)|node --test|generate-(?:route-manifest|openapi))\b)/mu,
  );
});
