import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { chmod, cp, mkdtemp, mkdir, readFile, readdir, rm, symlink, unlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test } from "vitest";

const run = promisify(execFile);
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const buildOutputDomain = "terrascale.deploy/candidate-build-output/v1";
const buildMediaType = "application/vnd.terrascale.candidate-build-receipt.v1+json";
const qualificationMediaType = "application/vnd.terrascale.npm-qualification.v2+json";
const npmArchiveIntegrity = "sha512-T67M4L5wNm0cZ7EBLErcEkY1SmzEW/WJ+SADBzsFUY1UdAPfFHXFQtZ6SEXiK0+vzXysCvAsepbMaBTwnrAD+w==";
const npmTreeDigestDomain = "terrascale/npm-cli-tree/v1";
const nodeVersions = { 22: "v22.23.2", 24: "v24.20.0" } as const;

test("release qualification mode requires its exact clean two-runtime contract", async () => {
  const qualifier = path.join(repositoryRoot, "scripts", "qualify-npm-artifact.ts");
  await assert.rejects(
    run("node", [qualifier, "--release", "--allow-dirty"]),
    /--allow-dirty cannot be combined with --release/u,
  );
  await assert.rejects(
    run("node", [qualifier, "--release"], {
      env: { PATH: process.env.PATH ?? "" },
    }),
    /TERRASCALE_NODE_22 must name an absolute Node executable/u,
  );
  await assert.rejects(
    run("node", [qualifier, "--release", "--node", process.execPath], {
      env: {
        PATH: process.env.PATH ?? "",
        TERRASCALE_NODE_22: process.execPath,
        TERRASCALE_NODE_24: process.execPath,
      },
    }),
    /--release obtains its exact Node executables/u,
  );
});

test("promotion accepts one fully bound candidate and rejects hostile receipt substitutions", async () => {
  const workspace = await mkdtemp(path.join(tmpdir(), "terrascale-npm-receipt-test-"));
  const checkout = path.join(workspace, "sdk");
  try {
    await cp(repositoryRoot, checkout, {
      recursive: true,
      filter: (source) => ![".git", ".tools", "artifacts", "dist", "node_modules"].includes(path.basename(source)),
    });
    await initializeGitCheckout(checkout);
    await mkdir(path.join(checkout, ".tools"));
    await writeFile(path.join(checkout, ".tools", "terrascale-deploy"), "downloaded workflow tool\n");

    const fixture = await createCandidateFixture(checkout, workspace);
    await fixture.verify();
    assert.equal((await run("git", ["status", "--porcelain", "--untracked-files=all"], { cwd: checkout })).stdout, "");

    const hostileCases: readonly {
      readonly name: string;
      readonly mutate: () => Promise<void>;
    }[] = [
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
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.source.revision = "0".repeat(40);
        }),
      },
      {
        name: "dirty source receipt",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.source.clean = false;
          receipt.source.dirtyStatusSha256 = "a".repeat(64);
        }),
      },
      {
        name: "single-runtime receipt",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.runtimes = receipt.runtimes.slice(0, 1);
        }),
      },
      {
        name: "runtime patch substitution",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.runtimes[0]!.version = "v22.23.1";
        }),
      },
      {
        name: "runtime policy substitution",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.release.nodeVersions["22"] = "v22.23.1";
        }),
      },
      {
        name: "npm CLI substitution",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.environment.npm.version = "11.18.1";
        }),
      },
      {
        name: "npm CLI package tree substitution",
        mutate: async () => {
          await writeFile(path.join(fixture.reviewedNpm.packageRoot, "substituted.txt"), "unreviewed npm bytes\n");
        },
      },
      {
        name: "mismatched tarball digest",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.artifact.sha256 = "b".repeat(64);
        }),
      },
      {
        name: "substituted tarball bytes",
        mutate: async () => {
          await writeFile(fixture.tarballPath, Buffer.concat([await readFile(fixture.tarballPath), Buffer.from("substituted")]));
          await fixture.refreshBuildReceipt();
        },
      },
      {
        name: "mismatched source tree",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.source.tree = "c".repeat(40);
        }),
      },
      {
        name: "mismatched package version",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.artifact.version = "9.9.9";
        }),
      },
      {
        name: "mismatched Dashboard contract",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.contract.openApiSha256 = "d".repeat(64);
        }),
      },
      {
        name: "release limitation",
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.limitations = ["not release eligible"];
        }),
      },
      {
        name: "diagnostic release receipt",
        mutate: async () => fixture.mutateQualification((receipt) => {
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
        mutate: async () => fixture.mutateQualification((receipt) => {
          receipt.unknown = true;
        }),
      },
      {
        name: "duplicate qualification field",
        mutate: async () => {
          const duplicated = fixture.qualificationBytes.toString("utf8").replace(
            '  "status": "passed",',
            '  "status": "passed",\n  "status": "passed",',
          );
          await writeFile(fixture.qualificationPath, duplicated);
          await fixture.refreshBuildReceipt();
        },
      },
      {
        name: "substituted qualification receipt",
        mutate: async () => {
          const receipt = structuredClone(fixture.qualification) as QualificationReceipt;
          receipt.artifact.sha256 = "e".repeat(64);
          await writeFile(fixture.qualificationPath, `${JSON.stringify(receipt, null, 2)}\n`);
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
          const receipt = structuredClone(fixture.buildReceipt) as BuildReceipt;
          receipt.output.files[0]!.digest = receipt.output.files[0]!.digest.toUpperCase();
          await writeFile(fixture.buildReceiptPath, `${JSON.stringify(receipt, null, 2)}\n`);
        },
      },
      {
        name: "extra candidate file",
        mutate: async () => {
          await writeFile(path.join(fixture.candidateDirectory, "extra.txt"), "not admitted\n");
        },
      },
      {
        name: "dirty promotion checkout",
        mutate: async () => {
          await writeFile(path.join(checkout, "uncommitted-source.txt"), "dirty\n");
        },
      },
    ];

    for (const hostileCase of hostileCases) {
      await fixture.reset();
      await hostileCase.mutate();
      await assert.rejects(fixture.verify(), `Promotion accepted hostile case: ${hostileCase.name}`);
      await rm(path.join(checkout, "uncommitted-source.txt"), { force: true });
    }

    await fixture.reset();
    await verifyIsolatedPublish(fixture, workspace);
  } finally {
    await rm(workspace, { recursive: true, force: true });
  }
});

async function initializeGitCheckout(checkout: string): Promise<void> {
  await run("git", ["init", "--quiet"], { cwd: checkout });
  await run("git", ["config", "user.email", "release-test@terrascale.test"], { cwd: checkout });
  await run("git", ["config", "user.name", "TerraScale Release Test"], { cwd: checkout });
  await run("git", ["add", "."], { cwd: checkout });
  await run("git", ["commit", "--quiet", "-m", "fixture"], { cwd: checkout });
}

async function createCandidateFixture(checkout: string, workspace: string): Promise<CandidateFixture> {
  const reviewedNpm = await createReviewedNpmFixture(workspace);
  const sourceManifest = JSON.parse(await readFile(path.join(checkout, "package.json"), "utf8")) as {
    readonly name: string;
    readonly version: string;
    readonly private: boolean;
    readonly exports: unknown;
  };
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
      prepublishOnly: "node -e \"require('node:fs').writeFileSync('lifecycle-ran', 'yes')\"",
    },
  };
  const packedManifestBytes = Buffer.from(`${JSON.stringify(packedManifest, null, 2)}\n`);
  const routeManifestBytes = await readFile(path.join(checkout, "sdk-route-manifest.json"));
  const licenseBytes = await readFile(path.join(checkout, "LICENSE"));
  await Promise.all([
    writeFile(path.join(packageDirectory, "package.json"), packedManifestBytes),
    writeFile(path.join(packageDirectory, "sdk-route-manifest.json"), routeManifestBytes),
    writeFile(path.join(packageDirectory, "LICENSE"), licenseBytes),
  ]);

  const tarballPath = path.join(candidateDirectory, `terrascale-sdk-${sourceManifest.version}.tgz`);
  const qualificationPath = path.join(candidateDirectory, `terrascale-sdk-${sourceManifest.version}.qualification.json`);
  const buildReceiptPath = path.join(evidenceDirectory, "npm.build-receipt.json");
  await run("tar", ["-czf", tarballPath, "-C", path.dirname(packageDirectory), "package"]);
  const tarballBytes = await readFile(tarballPath);
  const descriptorBytes = await readFile(path.join(checkout, "sdk-openapi-contract-descriptor.json"));
  const descriptor = JSON.parse(descriptorBytes.toString("utf8")) as {
    readonly descriptorVersion: number;
    readonly routeCount: number;
    readonly sources: {
      readonly openApiSha256: string;
      readonly routeManifestSha256: string;
    };
  };
  const promotionNodeSha256 = sha256(await readFile(process.execPath));
  const [revision, tree, treeListing] = await Promise.all([
    run("git", ["rev-parse", "HEAD"], { cwd: checkout }),
    run("git", ["rev-parse", "HEAD^{tree}"], { cwd: checkout }),
    run("git", ["ls-tree", "-r", "-z", "HEAD"], { cwd: checkout }),
  ]);
  const now = Date.now();
  const qualification = {
    schemaVersion: 2,
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
      unpackedSize: packedManifestBytes.length + routeManifestBytes.length + licenseBytes.length,
      fileCount: 3,
    },
    boundaries: {
      exports: sourceManifest.exports,
      browserEntrypointGraphs: {
        root: ["dist/index.js"],
        admin: ["dist/admin.js"],
      },
      betterAuthExport: "node-only",
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
    dependencies: { count: 0, packages: {} },
    license: { declared: "Apache-2.0", fileSha256: sha256(licenseBytes) },
    security: {
      audit: {
        vulnerabilities: { info: 0, low: 0, moderate: 0, high: 0, critical: 0, total: 0 },
      },
      registry: "https://registry.npmjs.org/",
    },
    command: "npm run qualify:artifact -- --release --output artifacts/candidate",
    environment: {
      platform: "linux",
      architecture: "x64",
      npm: reviewedNpm.evidence,
    },
    limitations: [],
    redaction: "No environment values or package contents are embedded; dirty paths and workflow tooling are represented only by bounded classification evidence.",
  } as QualificationReceipt;
  const qualificationBytes = Buffer.from(`${JSON.stringify(qualification, null, 2)}\n`);
  await writeFile(qualificationPath, qualificationBytes);

  const buildIdentity = {
    revision: revision.stdout.trim(),
    tree: tree.stdout.trim(),
    treeDigest: `sha256:${sha256(Buffer.from(treeListing.stdout))}`,
    startedAt: new Date(now - 3_000).toISOString(),
    completedAt: new Date(now).toISOString(),
  };

  async function createBuildReceipt(): Promise<BuildReceipt> {
    const files = await Promise.all((await readdir(candidateDirectory))
      .sort(ordinal)
      .map(async (name) => {
        const bytes = await readFile(path.join(candidateDirectory, name));
        return { path: name, digest: `sha256:${sha256(bytes)}`, bytes: bytes.length };
      }));
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
      repository: "TerraScale.TypeScript",
      source: {
        commit: buildIdentity.revision,
        tree: buildIdentity.tree,
        treeDigest: buildIdentity.treeDigest,
      },
      deployCli: {
        package: "TerraScale.Deploy.Cli",
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
  let buildReceiptBytes = Buffer.from(`${JSON.stringify(buildReceipt, null, 2)}\n`);
  await writeFile(buildReceiptPath, buildReceiptBytes);

  async function reset(): Promise<void> {
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

  async function refreshBuildReceipt(): Promise<void> {
    buildReceipt = await createBuildReceipt();
    await writeFile(buildReceiptPath, `${JSON.stringify(buildReceipt, null, 2)}\n`);
  }

  async function mutateQualification(mutator: (receipt: QualificationReceipt) => void): Promise<void> {
    const receipt = structuredClone(qualification) as QualificationReceipt;
    mutator(receipt);
    await writeFile(qualificationPath, `${JSON.stringify(receipt, null, 2)}\n`);
    await refreshBuildReceipt();
  }

  async function verify(environment: NodeJS.ProcessEnv = process.env): Promise<void> {
    await run("node", [
      path.join(checkout, "scripts", "promote-npm-candidate.ts"),
      "--build-receipt", buildReceiptPath,
      "--artifacts", candidateDirectory,
      "--registry", "https://registry.npmjs.org/",
      "--verify-only",
    ], {
      cwd: checkout,
      env: reviewedNpm.environment(environment),
    });
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
    get buildReceipt() { return buildReceipt; },
    get buildReceiptBytes() { return buildReceiptBytes; },
    reset,
    refreshBuildReceipt,
    mutateQualification,
    verify,
  };
}

async function verifyIsolatedPublish(fixture: CandidateFixture, workspace: string): Promise<void> {
  const { availabilityRecord, publishRecord } = fixture.reviewedNpm;
  await run("node", [
    path.join(fixture.checkout, "scripts", "promote-npm-candidate.ts"),
    "--build-receipt", fixture.buildReceiptPath,
    "--artifacts", fixture.candidateDirectory,
    "--registry", "https://registry.npmjs.org/",
  ], {
    cwd: fixture.checkout,
    env: fixture.reviewedNpm.environment({
      ...process.env,
      NPM_TOKEN: "test-token",
    }),
  });
  const availability = JSON.parse(await readFile(availabilityRecord, "utf8")) as NpmInvocation;
  const publish = JSON.parse(await readFile(publishRecord, "utf8")) as NpmInvocation;
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

async function createReviewedNpmFixture(workspace: string): Promise<ReviewedNpmFixture> {
  const root = path.join(workspace, "reviewed-npm");
  const packageRoot = path.join(root, "package");
  const entrypoint = path.join(packageRoot, "bin", "npm-cli.js");
  const binDirectory = path.join(root, "path-bin");
  const availabilityRecord = path.join(workspace, "availability.json");
  const publishRecord = path.join(workspace, "publish.json");
  const packageJsonBytes = Buffer.from(`${JSON.stringify({
    name: "npm",
    version: "11.18.0",
    bin: { npm: "bin/npm-cli.js" },
  }, null, 2)}\n`);
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
    { path: "bin/npm-cli.js", sha256: sha256(entrypointBytes), bytes: entrypointBytes.length },
    { path: "package.json", sha256: sha256(packageJsonBytes), bytes: packageJsonBytes.length },
  ];
  const evidence = {
    version: "11.18.0",
    archiveIntegrity: npmArchiveIntegrity,
    entrypoint: "bin/npm-cli.js",
    entrypointSha256: sha256(entrypointBytes),
    packageJsonSha256: sha256(packageJsonBytes),
    packageTreeSha256: sha256(Buffer.from(JSON.stringify({ domain: npmTreeDigestDomain, files }))),
    fileCount: files.length,
    bytes: files.reduce((total, file) => total + file.bytes, 0),
  };

  async function reset(): Promise<void> {
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

  function environment(base: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
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

function buildOutputDigest(files: readonly BuildFile[]): string {
  return `sha256:${sha256(Buffer.from(JSON.stringify({
    domain: buildOutputDomain,
    root: "artifacts/candidate",
    files,
  })))}`;
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function sha256(bytes: Buffer): string {
  return createHash("sha256").update(bytes).digest("hex");
}

type BuildFile = {
  path: string;
  digest: string;
  bytes: number;
};

type BuildReceipt = {
  schemaVersion: number;
  mediaType: string;
  repository: string;
  source: { commit: string; tree: string; treeDigest: string };
  deployCli: { package: string; version: string; executableDigest: string };
  target: string;
  output: {
    root: string;
    digest: string;
    fileCount: number;
    bytes: number;
    files: BuildFile[];
  };
  startedAt: string;
  completedAt: string;
};

type QualificationReceipt = {
  schemaVersion: number;
  mediaType: string;
  status: string;
  startedAt: string;
  completedAt: string;
  source: {
    revision: string;
    tree: string;
    clean: boolean;
    dirtyStatusSha256: string | null;
    toolingExclusion: { root: string; classification: string; trackedFiles: number };
  };
  release: { eligible: boolean; nodeVersions: Record<string, string> };
  artifact: {
    name: string;
    version: string;
    filename: string;
    sha256: string;
    integrity: string;
    size: number;
    unpackedSize: number;
    fileCount: number;
  };
  boundaries: unknown;
  contract: {
    descriptorVersion: number;
    descriptorSha256: string;
    openApiSha256: string;
    routeManifestSha256: string;
    routeCount: number;
  };
  runtimes: {
    major: number;
    executable: string;
    executableSha256: string;
    version: string;
    status: string;
  }[];
  dependencies: unknown;
  license: unknown;
  security: unknown;
  command: string;
  environment: {
    platform: string;
    architecture: string;
    npm: {
      version: string;
      archiveIntegrity: string;
      entrypoint: string;
      entrypointSha256: string;
      packageJsonSha256: string;
      packageTreeSha256: string;
      fileCount: number;
      bytes: number;
    };
  };
  limitations: string[];
  redaction: string;
  unknown?: boolean;
};

type CandidateFixture = {
  readonly checkout: string;
  readonly candidateDirectory: string;
  readonly tarballPath: string;
  readonly qualificationPath: string;
  readonly buildReceiptPath: string;
  readonly tarballBytes: Buffer;
  readonly qualification: QualificationReceipt;
  readonly qualificationBytes: Buffer;
  readonly reviewedNpm: ReviewedNpmFixture;
  readonly buildReceipt: BuildReceipt;
  readonly buildReceiptBytes: Buffer;
  readonly reset: () => Promise<void>;
  readonly refreshBuildReceipt: () => Promise<void>;
  readonly mutateQualification: (mutator: (receipt: QualificationReceipt) => void) => Promise<void>;
  readonly verify: (environment?: NodeJS.ProcessEnv) => Promise<void>;
};

type ReviewedNpmFixture = {
  readonly packageRoot: string;
  readonly binDirectory: string;
  readonly availabilityRecord: string;
  readonly publishRecord: string;
  readonly evidence: QualificationReceipt["environment"]["npm"];
  readonly reset: () => Promise<void>;
  readonly environment: (base: NodeJS.ProcessEnv) => NodeJS.ProcessEnv;
};

type NpmInvocation = {
  readonly arguments_: readonly string[];
  readonly cwd: string;
  readonly token?: string;
  readonly userConfig: string;
  readonly globalConfig: string;
  readonly userConfigHasAuth: boolean;
  readonly globalConfigHasAuth: boolean;
  readonly userConfigMode: number;
  readonly globalConfigMode: number;
  readonly ignoreScripts?: string;
};
