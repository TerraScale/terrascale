import { spawn, type ChildProcessByStdio } from "node:child_process";
import type { Readable } from "node:stream";

export type SDKFixtureEndpoints = {
  dashboardUrl: string;
  publicApiUrl: string;
};

export type RunningSDKFixture = {
  endpoints: SDKFixtureEndpoints;
  stop(): Promise<void>;
};

const fixtureStartupTimeoutMs = 60_000;
const fixtureShutdownTimeoutMs = 5_000;
type SDKFixtureProcess = ChildProcessByStdio<null, Readable, Readable>;

/** Starts the test-only two-server fixture and reads its single JSON startup line. */
export async function startSDKFixture(): Promise<RunningSDKFixture> {
  const child = spawn("go", ["run", "-mod=vendor", "./tests/integration/fixture"], {
    cwd: process.cwd(),
    env: { ...process.env, TS_DASHBOARD_PRODUCTION_DEMO: "1" },
    detached: process.platform !== "win32",
    stdio: ["ignore", "pipe", "pipe"],
  });

  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => {
    stderr = `${stderr}${chunk.toString()}`.slice(-16_384);
  });

  try {
    const endpoints = await readStartupLine(child, () => stderr);
    assertLoopbackHttpUrl(endpoints.dashboardUrl, "dashboardUrl");
    assertLoopbackHttpUrl(endpoints.publicApiUrl, "publicApiUrl");
    return { endpoints, stop: () => stopProcess(child) };
  } catch (error) {
    await stopProcess(child);
    throw error;
  }
}

function readStartupLine(
  child: SDKFixtureProcess,
  stderr: () => string,
): Promise<SDKFixtureEndpoints> {
  return new Promise((resolve, reject) => {
    let output = "";
    let settled = false;
    const timeout = setTimeout(() => {
      finish(new Error(`SDK fixture did not emit its startup JSON within ${fixtureStartupTimeoutMs} ms. ${stderr()}`));
      signalProcessTree(child, "SIGTERM");
    }, fixtureStartupTimeoutMs);

    const finish = (error?: Error, endpoints?: SDKFixtureEndpoints) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      child.stdout.off("data", onData);
      child.off("error", onError);
      child.off("exit", onExit);
      if (error) reject(error);
      else resolve(endpoints!);
    };

    const onData = (chunk: Buffer) => {
      output += chunk.toString();
      const newline = output.indexOf("\n");
      if (newline < 0) return;
      const line = output.slice(0, newline).trim();
      try {
        const value = JSON.parse(line) as Partial<SDKFixtureEndpoints>;
        if (typeof value.dashboardUrl !== "string" || typeof value.publicApiUrl !== "string") {
          throw new Error("startup JSON must contain dashboardUrl and publicApiUrl strings");
        }
        finish(undefined, { dashboardUrl: value.dashboardUrl, publicApiUrl: value.publicApiUrl });
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        finish(new Error(`Invalid SDK fixture startup line ${JSON.stringify(line)}: ${message}. ${stderr()}`));
        signalProcessTree(child, "SIGTERM");
      }
    };

    const onError = (error: Error) => finish(new Error(`Could not start SDK fixture: ${error.message}. ${stderr()}`));
    const onExit = (code: number | null, signal: NodeJS.Signals | null) => {
      finish(new Error(`SDK fixture exited before startup (code ${code}, signal ${signal}). ${stderr()}`));
    };

    child.stdout.on("data", onData);
    child.on("error", onError);
    child.on("exit", onExit);
  });
}

function assertLoopbackHttpUrl(value: string, name: string): void {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`SDK fixture ${name} must be an absolute URL.`);
  }
  if (url.protocol !== "http:" || !["127.0.0.1", "localhost", "[::1]"].includes(url.hostname)) {
    throw new Error(`SDK fixture ${name} must use loopback HTTP; got ${url.origin}.`);
  }
}

async function stopProcess(child: SDKFixtureProcess): Promise<void> {
  if (child.exitCode === null && child.signalCode === null) {
    const exited = new Promise<void>((resolve) => child.once("exit", () => resolve()));
    signalProcessTree(child, "SIGTERM");
    if (!(await settlesWithin(exited, fixtureShutdownTimeoutMs))) {
      signalProcessTree(child, "SIGKILL");
      child.kill("SIGKILL");
      await exited;
    }
  }
  if (child.pid !== undefined && process.platform !== "win32") {
    if (!(await waitForProcessGroupExit(child.pid, fixtureShutdownTimeoutMs))) {
      signalProcessGroup(child.pid, "SIGKILL");
    }
  }
}

function signalProcessTree(child: SDKFixtureProcess, signal: NodeJS.Signals): void {
  if (child.pid !== undefined && process.platform !== "win32") {
    signalProcessGroup(child.pid, signal);
  } else {
    child.kill(signal);
  }
}

function signalProcessGroup(processGroupId: number, signal: NodeJS.Signals): void {
  try {
    process.kill(-processGroupId, signal);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ESRCH") throw error;
  }
}

async function waitForProcessGroupExit(processGroupId: number, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      process.kill(-processGroupId, 0);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ESRCH") return true;
      throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  try {
    process.kill(-processGroupId, 0);
    return false;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ESRCH") return true;
    throw error;
  }
}

async function settlesWithin(promise: Promise<void>, timeoutMs: number): Promise<boolean> {
  let timeout: NodeJS.Timeout | undefined;
  const result = await Promise.race([
    promise.then(() => true),
    new Promise<false>((resolve) => {
      timeout = setTimeout(() => resolve(false), timeoutMs);
    }),
  ]);
  if (timeout) clearTimeout(timeout);
  return result;
}
