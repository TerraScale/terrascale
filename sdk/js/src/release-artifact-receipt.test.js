import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import {
  chmod,
  cp,
  mkdtemp,
  mkdir,
  readFile,
  readdir,
  rm,
  symlink,
  unlink,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test } from "vitest";
import { npmReleasePolicy } from "../scripts/npm-release-policy.js";

const run = promisify(execFile);
const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const buildOutputDomain = "terrascale.deploy/candidate-build-output/v1";
const buildMediaType =
  "application/vnd.terrascale.candidate-build-receipt.v1+json";
const qualificationMediaType =
  "application/vnd.terrascale.npm-qualification.v1+json";
const npmArchiveIntegrity =
  "sha512-T67M4L5wNm0cZ7EBLErcEkY1SmzEW/WJ+SADBzsFUY1UdAPfFHXFQtZ6SEXiK0+vzXysCvAsepbMaBTwnrAD+w==";
const npmTreeDigestDomain = "terrascale/npm-cli-tree/v1";
const nodeVersions = /** @type {const} */ ({ 22: "v22.23.2", 24: "v24.21.0" });

test("the planned Better Auth packed journey fails closed without a public stub", async () => {
  await assert.rejects(run(process.execPath, [path.join(repositoryRoot, "scripts", "packed-release-e2e-journey.js")]),
    /Full Better Auth account journey is planned and unqualified/u);
});

test("release qualification mode requires its exact clean two-runtime contract", async () => {
  const qualifier = path.join(
    repositoryRoot,
    "scripts",
    "qualify-npm-artifact.js",
  );
  await assert.rejects(
    run(process.execPath, [qualifier, "--release", "--allow-dirty"]),
    /--allow-dirty cannot be combined with --release/u,
  );
  await assert.rejects(
    run(process.execPath, [qualifier, "--release"], {
      env: { PATH: process.env.PATH ?? "" },
    }),
    /TERRASCALE_NODE_22 must name an absolute Node executable/u,
  );
  await assert.rejects(
    run(
      process.execPath,
      [qualifier, "--release", "--node", process.execPath],
      {
        env: {
          PATH: process.env.PATH ?? "",
          TERRASCALE_NODE_22: process.execPath,
          TERRASCALE_NODE_24: process.execPath,
        },
      },
    ),
    /--release obtains its exact Node executables/u,
  );
});

// Promotion validates the exact publish toolchain; it is reproducible only on
// the pinned Node 24 runtime and skips elsewhere (CI pins the matrix versions).
test.skipIf(process.version !== nodeVersions[24])(
  "promotion verifies bound candidates, rejects hostile substitutions and publishes through an isolated npm",
  async () => {
    const workspace = await mkdtemp(
      path.join(tmpdir(), "terrascale-npm-receipt-test-"),
    );
    const checkout = path.join(workspace, "sdk");
    try {
      await cp(repositoryRoot, checkout, {
        recursive: true,
        filter: (source) =>
          ![".git", ".tools", "artifacts", "types", "node_modules"].includes(
            path.basename(source),
          ),
      });
      await initializeGitCheckout(checkout);
      await mkdir(path.join(checkout, ".tools"));
      await writeFile(
        path.join(checkout, ".tools", "terrascale-deploy"),
        "downloaded workflow tool\n",
      );

      const fixture = await createCandidateFixture(checkout, workspace);
      await fixture.verify();
      assert.equal(
        (
          await run("git", ["status", "--porcelain", "--untracked-files=all"], {
            cwd: checkout,
          })
        ).stdout,
        "",
      );

      /** @type {readonly { name: string, mutate: () => Promise<void> }[]} */
      const hostileCases = [
        {
          name: "missing qualification receipt",
          mutate: async () => {
            await unlink(fixture.qualificationPath);
          },
        },
        {
          name: "missing build receipt",
          mutate: async () => {
            await unlink(fixture.buildReceiptPath);
          },
        },
        {
          name: "stale source revision",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.source.revision = "0".repeat(40);
            }),
        },
        {
          name: "dirty source receipt",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.source.clean = false;
              receipt.source.dirtyStatusSha256 = "a".repeat(64);
            }),
        },
        {
          name: "single-runtime receipt",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.runtimes = receipt.runtimes.slice(0, 1);
            }),
        },
        {
          name: "runtime patch substitution",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              const [runtime] = receipt.runtimes;
              assert.ok(runtime);
              runtime.version = "v22.23.1";
            }),
        },
        {
          name: "runtime policy substitution",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.release.nodeVersions["22"] = "v22.23.1";
            }),
        },
        {
          name: "runtime dependency license substitution",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              const ulid = receipt.dependencies.packages.ulid;
              assert.ok(ulid);
              ulid.fileSha256 = "0".repeat(64);
            }),
        },
        {
          name: "missing runtime dependency qualification",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.dependencies = { count: 0, packages: {} };
            }),
        },
        {
          name: "npm CLI substitution",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.environment.npm.version = "11.18.1";
            }),
        },
        {
          name: "npm CLI package tree substitution",
          mutate: async () => {
            await writeFile(
              path.join(fixture.reviewedNpm.packageRoot, "substituted.txt"),
              "unreviewed npm bytes\n",
            );
          },
        },
        {
          name: "mismatched tarball digest",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.artifact.sha256 = "b".repeat(64);
            }),
        },
        {
          name: "substituted tarball bytes",
          mutate: async () => {
            await writeFile(
              fixture.tarballPath,
              Buffer.concat([
                await readFile(fixture.tarballPath),
                Buffer.from("substituted"),
              ]),
            );
            await fixture.refreshBuildReceipt();
          },
        },
        {
          name: "mismatched source tree",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.source.tree = "c".repeat(40);
            }),
        },
        {
          name: "mismatched package version",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.artifact.version = "9.9.9";
            }),
        },
        {
          name: "mismatched native producer contract",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.contract.openApiSha256 = "d".repeat(64);
            }),
        },
        {
          name: "release limitation",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.limitations = ["not release eligible"];
            }),
        },
        {
          name: "diagnostic release receipt",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.release.eligible = false;
            }),
        },
        {
          name: "malformed qualification JSON",
          mutate: async () => {
            await writeFile(fixture.qualificationPath, "{");
            await fixture.refreshBuildReceipt();
          },
        },
        {
          name: "unknown qualification field",
          mutate: async () =>
            fixture.mutateQualification((receipt) => {
              receipt.unknown = true;
            }),
        },
        {
          name: "duplicate qualification field",
          mutate: async () => {
            const duplicated = fixture.qualificationBytes
              .toString("utf8")
              .replace(
                '  "status": "passed",',
                '  "status": "passed",\n  "status": "passed",',
              );
            await writeFile(fixture.qualificationPath, duplicated);
            await fixture.refreshBuildReceipt();
          },
        },
        {
          name: "Better Auth export claim contradicts packed manifest",
          mutate: () => fixture.mutateQualification((receipt) => {
            const boundaries = /** @type {{betterAuthExport: string}} */ (receipt.boundaries);
            boundaries.betterAuthExport = boundaries.betterAuthExport === "absent" ? "node-and-workerd-server-only" : "absent";
          }),
        },
        {
          name: "Better Auth graph contradicts packed manifest",
          mutate: () => fixture.mutateQualification((receipt) => {
            const graphs = /** @type {{browserEntrypointGraphs: {betterAuthWorkerd: string[]}}} */ (receipt.boundaries).browserEntrypointGraphs;
            graphs.betterAuthWorkerd = graphs.betterAuthWorkerd.length === 0 ? ["src/better-auth.js"] : [];
          }),
        },
        {
          name: "substituted qualification receipt",
          mutate: async () => {
            const receipt = structuredClone(fixture.qualification);
            receipt.artifact.sha256 = "e".repeat(64);
            await writeFile(
              fixture.qualificationPath,
              `${JSON.stringify(receipt, null, 2)}\n`,
            );
          },
        },
        {
          name: "symlinked qualification receipt",
          mutate: async () => {
            const target = path.join(workspace, "qualification-target.json");
            await writeFile(target, fixture.qualificationBytes);
            await unlink(fixture.qualificationPath);
            await symlink(target, fixture.qualificationPath);
          },
        },
        {
          name: "symlinked tarball",
          mutate: async () => {
            const target = path.join(workspace, "tarball-target.tgz");
            await writeFile(target, fixture.tarballBytes);
            await unlink(fixture.tarballPath);
            await symlink(target, fixture.tarballPath);
          },
        },
        {
          name: "symlinked build receipt",
          mutate: async () => {
            const target = path.join(workspace, "build-receipt-target.json");
            await writeFile(target, fixture.buildReceiptBytes);
            await unlink(fixture.buildReceiptPath);
            await symlink(target, fixture.buildReceiptPath);
          },
        },
        {
          name: "malformed build receipt",
          mutate: async () => {
            await writeFile(fixture.buildReceiptPath, "{");
          },
        },
        {
          name: "uppercase build digest",
          mutate: async () => {
            const receipt = structuredClone(fixture.buildReceipt);
            const [file] = receipt.output.files;
            assert.ok(file);
            file.digest = file.digest.toUpperCase();
            await writeFile(
              fixture.buildReceiptPath,
              `${JSON.stringify(receipt, null, 2)}\n`,
            );
          },
        },
        {
          name: "extra candidate file",
          mutate: async () => {
            await writeFile(
              path.join(fixture.candidateDirectory, "extra.txt"),
              "not admitted\n",
            );
          },
        },
        {
          name: "dirty promotion checkout",
          mutate: async () => {
            await writeFile(
              path.join(checkout, "uncommitted-source.txt"),
              "dirty\n",
            );
          },
        },
      ];

      for (const hostileCase of hostileCases) {
        await fixture.reset();
        await hostileCase.mutate();
        await assert.rejects(
          fixture.verify(),
          `Promotion accepted hostile case: ${hostileCase.name}`,
        );
        await rm(path.join(checkout, "uncommitted-source.txt"), {
          force: true,
        });
      }

      await fixture.reset();
      await verifyIsolatedPublish(fixture, workspace);
    } finally {
      await rm(workspace, { recursive: true, force: true });
    }
  },
);

/**
 * @param {string} checkout
 * @returns {Promise<void>}
 */
async function initializeGitCheckout(checkout) {
  await run("git", ["init", "--quiet"], { cwd: checkout });
  await run("git", ["config", "user.email", "release-test@terrascale.test"], {
    cwd: checkout,
  });
  await run("git", ["config", "user.name", "TerraScale Release Test"], {
    cwd: checkout,
  });
  await run("git", ["add", "."], { cwd: checkout });
  await run("git", ["commit", "--quiet", "-m", "fixture"], { cwd: checkout });
}

/**
 * @param {string} checkout
 * @param {string} workspace
 * @returns {Promise<CandidateFixture>}
 */
async function createCandidateFixture(checkout, workspace) {
  const reviewedNpm = await createReviewedNpmFixture(workspace);
  /** @type {{ name: string, version: string, private: boolean, exports: unknown }} */
  const sourceManifest = JSON.parse(
    await readFile(path.join(checkout, "package.json"), "utf8"),
  );
  const candidateDirectory = path.join(checkout, "artifacts", "candidate");
  const evidenceDirectory = path.join(checkout, "artifacts", "evidence");
  const packageDirectory = path.join(workspace, "packed", "package");
  await Promise.all([
    mkdir(candidateDirectory, { recursive: true }),
    mkdir(evidenceDirectory, { recursive: true }),
    mkdir(packageDirectory, { recursive: true }),
  ]);
  const packedManifest = {
    ...sourceManifest,
    scripts: {
      prepublishOnly:
        "node -e \"require('node:fs').writeFileSync('lifecycle-ran', 'yes')\"",
    },
  };
  const packedManifestBytes = Buffer.from(
    `${JSON.stringify(packedManifest, null, 2)}\n`,
  );
  const routeManifestBytes = await readFile(
    path.join(checkout, "sdk-route-manifest.json"),
  );
  const currentPinBytes = await readFile(
    path.join(checkout, "sdk-current-contract.json"),
  );
  const licenseBytes = await readFile(path.join(checkout, "LICENSE"));
  await Promise.all([
    writeFile(path.join(packageDirectory, "package.json"), packedManifestBytes),
    writeFile(
      path.join(packageDirectory, "sdk-route-manifest.json"),
      routeManifestBytes,
    ),
    writeFile(
      path.join(packageDirectory, "sdk-current-contract.json"),
      currentPinBytes,
    ),
    writeFile(path.join(packageDirectory, "LICENSE"), licenseBytes),
  ]);

  const tarballPath = path.join(
    candidateDirectory,
    `terrascale-sdk-${sourceManifest.version}.tgz`,
  );
  const qualificationPath = path.join(
    candidateDirectory,
    `terrascale-sdk-${sourceManifest.version}.qualification.json`,
  );
  const buildReceiptPath = path.join(
    evidenceDirectory,
    "npm.build-receipt.json",
  );
  await run("tar", [
    "-czf",
    tarballPath,
    "-C",
    path.dirname(packageDirectory),
    "package",
  ]);
  const tarballBytes = await readFile(tarballPath);
  const descriptorBytes = await readFile(
    path.join(checkout, "sdk-openapi-contract-descriptor.json"),
  );
  /**
   * @type {{
   *   descriptorVersion: number,
   *   routeCount: number,
   *   sources: { openApiSha256: string, routeManifestSha256: string },
   * }}
   */
  const descriptor = JSON.parse(descriptorBytes.toString("utf8"));
  const promotionNodeSha256 = sha256(await readFile(process.execPath));
  const [revision, tree, treeListing] = await Promise.all([
    run("git", ["rev-parse", "HEAD"], { cwd: checkout }),
    run("git", ["rev-parse", "HEAD^{tree}"], { cwd: checkout }),
    run("git", ["ls-tree", "-r", "-z", "HEAD"], { cwd: checkout }),
  ]);
  const now = Date.now();
  const hasBetterAuthExport = Object.hasOwn(/** @type {object} */ (sourceManifest.exports), "./better-auth");
  /** @type {QualificationReceipt} */
  const qualification = {
    schemaVersion: 1,
    mediaType: qualificationMediaType,
    status: "passed",
    startedAt: new Date(now - 2_000).toISOString(),
    completedAt: new Date(now - 1_000).toISOString(),
    source: {
      revision: revision.stdout.trim(),
      tree: tree.stdout.trim(),
      clean: true,
      dirtyStatusSha256: null,
      toolingExclusion: {
        root: ".tools",
        classification: "ignored-workflow-tooling",
        trackedFiles: 0,
      },
    },
    release: {
      eligible: true,
      nodeVersions: { 22: nodeVersions[22], 24: nodeVersions[24] },
    },
    artifact: {
      name: sourceManifest.name,
      version: sourceManifest.version,
      filename: path.basename(tarballPath),
      sha256: sha256(tarballBytes),
      integrity: `sha512-${createHash("sha512").update(tarballBytes).digest("base64")}`,
      size: tarballBytes.length,
      unpackedSize:
        packedManifestBytes.length +
        routeManifestBytes.length +
        currentPinBytes.length +
        licenseBytes.length,
      fileCount: 4,
    },
    boundaries: {
      exports: sourceManifest.exports,
      browserEntrypointGraphs: {
        root: ["src/index.js"],
        admin: ["src/admin.js"],
        betterAuthWorkerd: hasBetterAuthExport ? ["src/better-auth.js"] : [],
      },
      betterAuthExport: hasBetterAuthExport ? "node-and-workerd-server-only" : "absent",
      packedSecretScan: "passed",
    },
    contract: {
      descriptorVersion: descriptor.descriptorVersion,
      descriptorSha256: sha256(descriptorBytes),
      openApiSha256: descriptor.sources.openApiSha256,
      routeManifestSha256: descriptor.sources.routeManifestSha256,
      routeCount: descriptor.routeCount,
    },
    runtimes: [
      {
        major: 22,
        executable: "/opt/hostedtoolcache/node/22.23.2/x64/bin/node",
        executableSha256: "1".repeat(64),
        version: nodeVersions[22],
        status: "passed",
      },
      {
        major: 24,
        executable: process.execPath,
        executableSha256: promotionNodeSha256,
        version: nodeVersions[24],
        status: "passed",
      },
    ],
    dependencies: { count: 1, packages: structuredClone(npmReleasePolicy.runtimeDependencies) },
    license: { declared: "Apache-2.0", fileSha256: sha256(licenseBytes) },
    security: {
      audit: {
        vulnerabilities: {
          info: 0,
          low: 0,
          moderate: 0,
          high: 0,
          critical: 0,
          total: 0,
        },
      },
      registry: "https://registry.npmjs.org/",
    },
    command:
      "npm run qualify:artifact -- --release --output artifacts/candidate",
    environment: {
      platform: "linux",
      architecture: "x64",
      npm: reviewedNpm.evidence,
    },
    limitations: [],
    redaction:
      "No environment values or package contents are embedded; dirty paths and workflow tooling are represented only by bounded classification evidence.",
  };
  const qualificationBytes = Buffer.from(
    `${JSON.stringify(qualification, null, 2)}\n`,
  );
  await writeFile(qualificationPath, qualificationBytes);

  const buildIdentity = {
    revision: revision.stdout.trim(),
    tree: tree.stdout.trim(),
    treeDigest: `sha256:${sha256(Buffer.from(treeListing.stdout))}`,
    startedAt: new Date(now - 3_000).toISOString(),
    completedAt: new Date(now).toISOString(),
  };

  /** @returns {Promise<BuildReceipt>} */
  async function createBuildReceipt() {
    const files = await Promise.all(
      (await readdir(candidateDirectory)).sort(ordinal).map(async (name) => {
        const bytes = await readFile(path.join(candidateDirectory, name));
        return {
          path: name,
          digest: `sha256:${sha256(bytes)}`,
          bytes: bytes.length,
        };
      }),
    );
    const output = {
      root: "artifacts/candidate",
      digest: buildOutputDigest(files),
      fileCount: files.length,
      bytes: files.reduce((total, file) => total + file.bytes, 0),
      files,
    };
    return {
      schemaVersion: 1,
      mediaType: buildMediaType,
      repository: "terrascale",
      source: {
        commit: buildIdentity.revision,
        tree: buildIdentity.tree,
        treeDigest: buildIdentity.treeDigest,
      },
      deployCli: {
        package: "ts-deploy",
        version: "1.1.0",
        executableDigest: `sha256:${"3".repeat(64)}`,
      },
      target: "npm",
      output,
      startedAt: buildIdentity.startedAt,
      completedAt: buildIdentity.completedAt,
    };
  }

  let buildReceipt = await createBuildReceipt();
  let buildReceiptBytes = Buffer.from(
    `${JSON.stringify(buildReceipt, null, 2)}\n`,
  );
  await writeFile(buildReceiptPath, buildReceiptBytes);

  /** @returns {Promise<void>} */
  async function reset() {
    await Promise.all([
      rm(path.join(checkout, "artifacts"), { recursive: true, force: true }),
      reviewedNpm.reset(),
    ]);
    await Promise.all([
      mkdir(candidateDirectory, { recursive: true }),
      mkdir(evidenceDirectory, { recursive: true }),
    ]);
    await Promise.all([
      writeFile(tarballPath, tarballBytes),
      writeFile(qualificationPath, qualificationBytes),
      writeFile(buildReceiptPath, buildReceiptBytes),
    ]);
  }

  /** @returns {Promise<void>} */
  async function refreshBuildReceipt() {
    buildReceipt = await createBuildReceipt();
    await writeFile(
      buildReceiptPath,
      `${JSON.stringify(buildReceipt, null, 2)}\n`,
    );
  }

  /**
   * @param {(receipt: QualificationReceipt) => void} mutator
   * @returns {Promise<void>}
   */
  async function mutateQualification(mutator) {
    const receipt = structuredClone(qualification);
    mutator(receipt);
    await writeFile(qualificationPath, `${JSON.stringify(receipt, null, 2)}\n`);
    await refreshBuildReceipt();
  }

  /**
   * @param {NodeJS.ProcessEnv} [environment]
   * @returns {Promise<void>}
   */
  async function verify(environment = process.env) {
    await run(
      process.execPath,
      [
        path.join(checkout, "scripts", "promote-npm-candidate.js"),
        "--build-receipt",
        buildReceiptPath,
        "--artifacts",
        candidateDirectory,
        "--registry",
        "https://registry.npmjs.org/",
        "--verify-only",
      ],
      {
        cwd: checkout,
        env: reviewedNpm.environment(environment),
      },
    );
  }

  return {
    checkout,
    candidateDirectory,
    tarballPath,
    qualificationPath,
    buildReceiptPath,
    tarballBytes,
    qualification,
    qualificationBytes,
    reviewedNpm,
    get buildReceipt() {
      return buildReceipt;
    },
    get buildReceiptBytes() {
      return buildReceiptBytes;
    },
    reset,
    refreshBuildReceipt,
    mutateQualification,
    verify,
  };
}

/**
 * @param {CandidateFixture} fixture
 * @param {string} workspace
 * @returns {Promise<void>}
 */
async function verifyIsolatedPublish(fixture, workspace) {
  const { availabilityRecord, publishRecord } = fixture.reviewedNpm;
  await run(
    process.execPath,
    [
      path.join(fixture.checkout, "scripts", "promote-npm-candidate.js"),
      "--build-receipt",
      fixture.buildReceiptPath,
      "--artifacts",
      fixture.candidateDirectory,
      "--registry",
      "https://registry.npmjs.org/",
    ],
    {
      cwd: fixture.checkout,
      env: fixture.reviewedNpm.environment({
        ...process.env,
        NPM_TOKEN: "test-token",
      }),
    },
  );
  /** @type {NpmInvocation} */
  const availability = JSON.parse(await readFile(availabilityRecord, "utf8"));
  /** @type {NpmInvocation} */
  const publish = JSON.parse(await readFile(publishRecord, "utf8"));
  assert.equal(availability.token, undefined);
  assert.equal(availability.userConfigHasAuth, false);
  assert.equal(availability.globalConfigHasAuth, false);
  assert.equal(availability.userConfigMode, 0o600);
  assert.equal(availability.globalConfigMode, 0o600);
  assert.equal(availability.arguments_.includes("--ignore-scripts"), true);
  assert.equal(publish.token, undefined);
  assert.equal(publish.userConfigHasAuth, true);
  assert.equal(publish.globalConfigHasAuth, false);
  assert.equal(publish.userConfigMode, 0o600);
  assert.equal(publish.globalConfigMode, 0o600);
  assert.equal(publish.ignoreScripts, "true");
  assert.equal(publish.arguments_.includes("--ignore-scripts"), true);
  assert.notEqual(availability.cwd, fixture.checkout);
  assert.notEqual(publish.cwd, fixture.checkout);
  await assert.rejects(readFile(path.join(workspace, "lifecycle-ran")));
}

/**
 * @param {string} workspace
 * @returns {Promise<ReviewedNpmFixture>}
 */
async function createReviewedNpmFixture(workspace) {
  const root = path.join(workspace, "reviewed-npm");
  const packageRoot = path.join(root, "package");
  const entrypoint = path.join(packageRoot, "bin", "npm-cli.js");
  const binDirectory = path.join(root, "path-bin");
  const availabilityRecord = path.join(workspace, "availability.json");
  const publishRecord = path.join(workspace, "publish.json");
  const packageJsonBytes = Buffer.from(
    `${JSON.stringify(
      {
        name: "npm",
        version: "11.18.0",
        bin: { npm: "bin/npm-cli.js" },
      },
      null,
      2,
    )}\n`,
  );
  const entrypointBytes = Buffer.from(`#!/usr/bin/env node
const { readFileSync, statSync, writeFileSync } = require("node:fs");
const [command, ...arguments_] = process.argv.slice(2);
if (command === "--version") {
  process.stdout.write("11.18.0\\n");
  process.exit(0);
}
const userConfig = process.env.NPM_CONFIG_USERCONFIG;
const globalConfig = process.env.NPM_CONFIG_GLOBALCONFIG;
const record = {
  arguments_,
  cwd: process.cwd(),
  token: process.env.NPM_TOKEN,
  userConfig,
  globalConfig,
  userConfigHasAuth: /_authToken=/.test(readFileSync(userConfig, "utf8")),
  globalConfigHasAuth: /_authToken=/.test(readFileSync(globalConfig, "utf8")),
  userConfigMode: statSync(userConfig).mode & 0o777,
  globalConfigMode: statSync(globalConfig).mode & 0o777,
  ignoreScripts: process.env.NPM_CONFIG_IGNORE_SCRIPTS,
};
if (command === "view") {
  writeFileSync(${JSON.stringify(availabilityRecord)}, JSON.stringify(record));
  process.stderr.write("npm error code E404\\n");
  process.exit(1);
}
if (command === "publish") {
  writeFileSync(${JSON.stringify(publishRecord)}, JSON.stringify(record));
  process.exit(0);
}
process.exit(2);
`);
  const files = [
    {
      path: "bin/npm-cli.js",
      sha256: sha256(entrypointBytes),
      bytes: entrypointBytes.length,
    },
    {
      path: "package.json",
      sha256: sha256(packageJsonBytes),
      bytes: packageJsonBytes.length,
    },
  ];
  const evidence = {
    version: "11.18.0",
    archiveIntegrity: npmArchiveIntegrity,
    entrypoint: "bin/npm-cli.js",
    entrypointSha256: sha256(entrypointBytes),
    packageJsonSha256: sha256(packageJsonBytes),
    packageTreeSha256: sha256(
      Buffer.from(JSON.stringify({ domain: npmTreeDigestDomain, files })),
    ),
    fileCount: files.length,
    bytes: files.reduce((total, file) => total + file.bytes, 0),
  };

  /** @returns {Promise<void>} */
  async function reset() {
    await rm(root, { recursive: true, force: true });
    await Promise.all([
      mkdir(path.dirname(entrypoint), { recursive: true }),
      mkdir(binDirectory, { recursive: true }),
    ]);
    await Promise.all([
      writeFile(path.join(packageRoot, "package.json"), packageJsonBytes),
      writeFile(entrypoint, entrypointBytes),
    ]);
    await chmod(entrypoint, 0o755);
    await symlink(entrypoint, path.join(binDirectory, "npm"));
  }

  /**
   * @param {NodeJS.ProcessEnv} base
   * @returns {NodeJS.ProcessEnv}
   */
  function environment(base) {
    return {
      ...base,
      PATH: `${binDirectory}${path.delimiter}${base.PATH ?? ""}`,
      TERRASCALE_NPM_ARCHIVE_INTEGRITY: npmArchiveIntegrity,
    };
  }

  await reset();
  return {
    packageRoot,
    binDirectory,
    availabilityRecord,
    publishRecord,
    evidence,
    reset,
    environment,
  };
}

/**
 * @param {readonly BuildFile[]} files
 * @returns {string}
 */
function buildOutputDigest(files) {
  return `sha256:${sha256(
    Buffer.from(
      JSON.stringify({
        domain: buildOutputDomain,
        root: "artifacts/candidate",
        files,
      }),
    ),
  )}`;
}

/**
 * @param {string} left
 * @param {string} right
 * @returns {number}
 */
function ordinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

/**
 * @param {Buffer} bytes
 * @returns {string}
 */
function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

/**
 * @typedef {object} BuildFile
 * @property {string} path
 * @property {string} digest
 * @property {number} bytes
 */

/**
 * @typedef {object} BuildReceipt
 * @property {number} schemaVersion
 * @property {string} mediaType
 * @property {string} repository
 * @property {{ commit: string, tree: string, treeDigest: string }} source
 * @property {{ package: string, version: string, executableDigest: string }} deployCli
 * @property {string} target
 * @property {{ root: string, digest: string, fileCount: number, bytes: number, files: BuildFile[] }} output
 * @property {string} startedAt
 * @property {string} completedAt
 */

/**
 * @typedef {object} QualifiedNpm
 * @property {string} version
 * @property {string} archiveIntegrity
 * @property {string} entrypoint
 * @property {string} entrypointSha256
 * @property {string} packageJsonSha256
 * @property {string} packageTreeSha256
 * @property {number} fileCount
 * @property {number} bytes
 */

/**
 * @typedef {object} QualificationReceipt
 * @property {number} schemaVersion
 * @property {string} mediaType
 * @property {string} status
 * @property {string} startedAt
 * @property {string} completedAt
 * @property {{
 *   revision: string,
 *   tree: string,
 *   clean: boolean,
 *   dirtyStatusSha256: string | null,
 *   toolingExclusion: { root: string, classification: string, trackedFiles: number },
 * }} source
 * @property {{ eligible: boolean, nodeVersions: Record<string, string> }} release
 * @property {{
 *   name: string,
 *   version: string,
 *   filename: string,
 *   sha256: string,
 *   integrity: string,
 *   size: number,
 *   unpackedSize: number,
 *   fileCount: number,
 * }} artifact
 * @property {unknown} boundaries
 * @property {{
 *   descriptorVersion: number,
 *   descriptorSha256: string,
 *   openApiSha256: string,
 *   routeManifestSha256: string,
 *   routeCount: number,
 * }} contract
 * @property {{
 *   major: number,
 *   executable: string,
 *   executableSha256: string,
 *   version: string,
 *   status: string,
 * }[]} runtimes
 * @property {{count: number, packages: Record<string, {version: string, license: string, fileSha256: string}>}} dependencies
 * @property {unknown} license
 * @property {unknown} security
 * @property {string} command
 * @property {{ platform: string, architecture: string, npm: QualifiedNpm }} environment
 * @property {string[]} limitations
 * @property {string} redaction
 * @property {boolean} [unknown]
 */

/**
 * @typedef {object} CandidateFixture
 * @property {string} checkout
 * @property {string} candidateDirectory
 * @property {string} tarballPath
 * @property {string} qualificationPath
 * @property {string} buildReceiptPath
 * @property {Buffer} tarballBytes
 * @property {QualificationReceipt} qualification
 * @property {Buffer} qualificationBytes
 * @property {ReviewedNpmFixture} reviewedNpm
 * @property {BuildReceipt} buildReceipt
 * @property {Buffer} buildReceiptBytes
 * @property {() => Promise<void>} reset
 * @property {() => Promise<void>} refreshBuildReceipt
 * @property {(mutator: (receipt: QualificationReceipt) => void) => Promise<void>} mutateQualification
 * @property {(environment?: NodeJS.ProcessEnv) => Promise<void>} verify
 */

/**
 * @typedef {object} ReviewedNpmFixture
 * @property {string} packageRoot
 * @property {string} binDirectory
 * @property {string} availabilityRecord
 * @property {string} publishRecord
 * @property {QualifiedNpm} evidence
 * @property {() => Promise<void>} reset
 * @property {(base: NodeJS.ProcessEnv) => NodeJS.ProcessEnv} environment
 */

/**
 * @typedef {object} NpmInvocation
 * @property {readonly string[]} arguments_
 * @property {string} cwd
 * @property {string} [token]
 * @property {string} userConfig
 * @property {string} globalConfig
 * @property {boolean} userConfigHasAuth
 * @property {boolean} globalConfigHasAuth
 * @property {number} userConfigMode
 * @property {number} globalConfigMode
 * @property {string} [ignoreScripts]
 */
