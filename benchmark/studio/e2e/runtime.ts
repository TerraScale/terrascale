// E2E harness plumbing: randomized loopback ports for the fixture servers and
// the built studio app, with health-gated startup and child cleanup. No fixed
// ports anywhere; the runtime map is exchanged through .e2e-runtime.json.

import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import { createServer as createTcpServer, type Server as TcpServer } from "node:net";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const STUDIO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const RUNTIME_FILE = resolve(STUDIO_ROOT, ".e2e-runtime.json");

export interface E2eRuntime {
  studioUrl: string;
  studioResyncUrl: string;
  fixtureUrl: string;
  fixtureResyncUrl: string;
}

const children: ChildProcess[] = [];

export function readRuntimeFile(): E2eRuntime {
  return JSON.parse(readFileSync(RUNTIME_FILE, "utf8")) as E2eRuntime;
}

export function writeRuntimeFile(runtime: E2eRuntime): void {
  writeFileSync(RUNTIME_FILE, JSON.stringify(runtime, null, 2), "utf8");
}

async function allocatePort(): Promise<number> {
  return new Promise((resolveListen, rejectListen) => {
    const server: TcpServer = createTcpServer();
    server.once("error", rejectListen);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (address === null || typeof address === "string") {
        server.close(() => rejectListen(new Error("no port allocated")));
        return;
      }
      const { port } = address;
      server.close(() => resolveListen(port));
    });
  });
}

async function waitForHttp(
  url: string,
  timeoutMs: number,
  accept: (status: number) => boolean,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError = "no attempt";
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, { redirect: "manual" });
      if (accept(response.status)) {
        return;
      }
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 150));
  }
  throw new Error(`waiting for ${url} timed out: ${lastError}`);
}

function spawnTracked(command: string, args: string[], env: NodeJS.ProcessEnv): ChildProcess {
  const child = spawn(command, args, {
    cwd: STUDIO_ROOT,
    env: { ...process.env, ...env },
    stdio: ["ignore", "pipe", "pipe"],
  });
  child.stdout?.on("data", (chunk: Buffer) => {
    process.stdout.write(`[child:${command}] ${chunk.toString("utf8")}`);
  });
  child.stderr?.on("data", (chunk: Buffer) => {
    process.stderr.write(`[child:${command}:err] ${chunk.toString("utf8")}`);
  });
  children.push(child);
  return child;
}

/** Builds the studio once (vp-mediated, cached by vp run inputs). */
export function buildStudio(): void {
  const result = spawnSync("vp", ["run", "vinext:build"], {
    cwd: STUDIO_ROOT,
    encoding: "utf8",
    env: { ...process.env },
  });
  if (result.status !== 0) {
    throw new Error(`vinext:build failed:\n${result.stdout}\n${result.stderr}`);
  }
}

interface FixtureSpec {
  readonly name: string;
  readonly tickIntervalMs: number;
  readonly ticksBeforeDrop: number;
  readonly heartbeatIntervalMs: number;
  readonly sessionBufferFrames: number;
  readonly statusDelayMs: number;
}

async function startFixture(spec: FixtureSpec): Promise<string> {
  const port = await allocatePort();
  spawnTracked(process.execPath, ["scripts/fixture-server.mjs"], {
    STUDIO_FIXTURE_HOST: "127.0.0.1",
    STUDIO_FIXTURE_PORT: String(port),
    FIXTURE_TICK_INTERVAL_MS: String(spec.tickIntervalMs),
    FIXTURE_TICKS_BEFORE_DROP: String(spec.ticksBeforeDrop),
    FIXTURE_HEARTBEAT_INTERVAL_MS: String(spec.heartbeatIntervalMs),
    FIXTURE_SESSION_BUFFER: String(spec.sessionBufferFrames),
    FIXTURE_STATUS_DELAY_MS: String(spec.statusDelayMs),
    FIXTURE_SESSION_IDLE_TTL_MS: "15000",
  });
  const baseUrl = `http://127.0.0.1:${port}`;
  await waitForHttp(`${baseUrl}/api/v2/_fixture/health`, 15_000, (status) => status === 200);
  return baseUrl;
}

async function startStudio(apiBaseUrl: string): Promise<string> {
  const port = await allocatePort();
  spawnTracked(process.execPath, ["dist/standalone/server.js"], {
    HOSTNAME: "127.0.0.1",
    PORT: String(port),
    STUDIO_API_BASE_URL: apiBaseUrl,
  });
  const baseUrl = `http://127.0.0.1:${port}`;
  await waitForHttp(baseUrl, 30_000, (status) => status === 200 || status === 500);
  return baseUrl;
}

/** Boots both fixtures and two studio instances (one per fixture). */
export async function bootstrapE2E(): Promise<E2eRuntime> {
  buildStudio();
  const fixtureUrl = await startFixture({
    name: "main",
    tickIntervalMs: 100,
    ticksBeforeDrop: 8,
    heartbeatIntervalMs: 1_500,
    sessionBufferFrames: 4_096,
    statusDelayMs: 1_500,
  });
  const fixtureResyncUrl = await startFixture({
    name: "resync",
    tickIntervalMs: 80,
    ticksBeforeDrop: 5,
    heartbeatIntervalMs: 60_000,
    sessionBufferFrames: 2,
    statusDelayMs: 0,
  });
  const studioUrl = await startStudio(fixtureUrl);
  const studioResyncUrl = await startStudio(fixtureResyncUrl);
  return { studioUrl, studioResyncUrl, fixtureUrl, fixtureResyncUrl };
}

export async function teardownE2E(): Promise<void> {
  for (const child of children.splice(0)) {
    child.kill("SIGTERM");
  }
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 250));
  for (const child of children) {
    child.kill("SIGKILL");
  }
}
