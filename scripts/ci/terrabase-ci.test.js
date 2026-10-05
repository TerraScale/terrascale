import { expect, test } from "vitest";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { access, readFile, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { captureProducerOutput, stopChild } from "./terrabase-lifecycle.mjs";
import { releaseApiUrl, releaseRedirectUrl } from "./provision-terrabase.mjs";

test("producer diagnostics keep a bounded tail and preserve split startup metadata", () => {
  const output = { log: "", started: false, admin: "", clientPort: "", pending: { stdout: "", stderr: "" } };
  captureProducerOutput(output, Buffer.from("Now listening on: http://127.0.0.1:184"));
  expect(output.admin).toBe("");
  captureProducerOutput(output, "47\nNow listening on: https://localhost:184");
  expect(output.clientPort).toBe("");
  captureProducerOutput(output, "48\nApplication star");
  captureProducerOutput(output, "ted.\n" + "x".repeat(128 * 1024));
  expect(output).toMatchObject({ log: "x".repeat(64 * 1024), started: true, admin: "http://127.0.0.1:18447", clientPort: "18448" });
  expect(output.pending.stdout.length).toBe(64 * 1024);
  captureProducerOutput(output, "latest diagnostic");
  expect(output.log.length).toBe(64 * 1024);
  expect(output.log.endsWith("latest diagnostic")).toBe(true);
  expect(output.admin).toBe("http://127.0.0.1:18447");
});

test("interleaved streams cannot corrupt split listeners or evict startup metadata", () => {
  const output = { log: "", started: false, admin: "", clientPort: "", pending: { stdout: "", stderr: "" } };
  captureProducerOutput(output, "Now listening on: http://127.0.0.1:184", "stdout");
  captureProducerOutput(output, "verbose diagnostic\n".repeat(10_000), "stderr");
  captureProducerOutput(output, "47\nNow listening on: https://localhost:18448\nApplication started.\n" + "verbose diagnostic\n".repeat(10_000), "stdout");
  expect(output).toMatchObject({ started: true, admin: "http://127.0.0.1:18447", clientPort: "18448" });
  expect(output.log).not.toContain("Now listening on");
  for (let i = 0; i < 100; i++) {
    captureProducerOutput(output, "x".repeat(128 * 1024), i % 2 === 0 ? "stdout" : "stderr");
    expect(output.log.length).toBe(64 * 1024);
    expect(output.pending.stdout.length).toBeLessThanOrEqual(64 * 1024);
    expect(output.pending.stderr.length).toBeLessThanOrEqual(64 * 1024);
  }
  captureProducerOutput(output, "latest diagnostic\n", "stderr");
  expect(output.log.endsWith("latest diagnostic\n")).toBe(true);
  expect(output).toMatchObject({ started: true, admin: "http://127.0.0.1:18447", clientPort: "18448" });
});

test("cleanup finishes after signal exit, spawn failure, and a live child", async () => {
  const child = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], { stdio: "ignore" });
  try {
    await once(child, "spawn");
    const exited = once(child, "exit");
    child.kill("SIGTERM");
    await exited;
    expect(child.exitCode).toBeNull();
    expect(child.signalCode).toBe("SIGTERM");
    await stopChild(child);
  } finally {
    child.kill("SIGKILL");
  }

  const failed = spawn(process.execPath, [], { cwd: "/nonexistent/ts498-spawn-failure", stdio: "ignore" });
  const [error] = await once(failed, "error");
  expect(error).toMatchObject({ code: "ENOENT" });
  expect(failed.pid).toBeUndefined();
  await stopChild(failed);

  const live = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], { stdio: "ignore" });
  try {
    await once(live, "spawn");
    await stopChild(live);
    expect(live.signalCode).toBe("SIGTERM");
  } finally {
    live.kill("SIGKILL");
  }
}, 2000);

test.skipIf(process.platform !== "linux").each(["zombie", "stopped"])("group cleanup handles a %s descendant after its leader exits", async state => {
  // The keeper leaves the group and retains its zombie until explicitly told to
  // reap it. This works without PID namespaces or relying on container PID 1.
  const fixture = `
import ctypes, os, signal, sys
group = os.getpgrp()
read, write = os.pipe()
keeper = os.fork()
if keeper:
    os.close(write)
    print(os.read(read, 100).decode(), flush=True)
    os._exit(0)
os.close(read)
os.setpgid(0, 0)
descendant = os.fork()
if descendant == 0:
    os.setpgid(0, group)
    ctypes.CDLL(None).prctl(15, b"ci ) (fixture)", 0, 0, 0)
    if sys.argv[1] == "stopped":
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        os.kill(os.getpid(), signal.SIGSTOP)
    os._exit(0)
os.waitid(os.P_PID, descendant, (os.WSTOPPED if sys.argv[1] == "stopped" else os.WEXITED) | os.WNOWAIT)
def cleanup(signum, frame):
    os.kill(descendant, signal.SIGKILL)
    os.waitpid(descendant, 0)
    os._exit(0)
signal.signal(signal.SIGTERM, cleanup)
os.write(write, f"{os.getpid()} {descendant}".encode())
os.close(write)
while True:
    signal.pause()
`;
  const leader = spawn("python3", ["-c", fixture, state], { detached: true, stdio: ["ignore", "pipe", "inherit"] });
  let output = "";
  leader.stdout.on("data", chunk => { output += chunk.toString(); });
  try {
    expect(await once(leader, "exit")).toEqual([0, null]);
    const [keeper, descendant] = output.trim().split(/\s+/u).map(Number);
    expect(keeper).toBeGreaterThan(0);
    expect(descendant).toBeGreaterThan(0);
    const stat = await readFile(`/proc/${descendant}/stat`, "utf8");
    expect(stat).toContain("(ci ) (fixture))");
    expect(stat.slice(stat.lastIndexOf(")") + 2).split(" ")[0]).toBe(state === "zombie" ? "Z" : "T");
    expect(() => process.kill(-(leader.pid ?? 0), 0)).not.toThrow();
    await stopChild(leader, true);
    const zombieStat = await readFile(`/proc/${descendant}/stat`, "utf8");
    expect(zombieStat.slice(zombieStat.lastIndexOf(")") + 2).split(" ")[0]).toBe("Z");
    expect(() => process.kill(-(leader.pid ?? 0), 0)).not.toThrow();
    await stopChild(leader, true);
  } finally {
    const [keeper] = output.trim().split(/\s+/u).map(Number);
    if (keeper) process.kill(keeper, "SIGTERM");
    else await stopChild(leader, true);
  }
}, 25_000);

test("release acquisition derives its API from an HTTPS registry origin", () => {
  expect(releaseApiUrl({ registryOrigin: "https://registry.example:8443", producer: "TerraScale/terrabase" }))
    .toBe("https://registry.example:8443/api/v1/repos/TerraScale/terrabase");
  for (const registryOrigin of ["relative", "https:registry.example", "http://registry.example", "https://user:password@registry.example", "https://registry.example/path", "https://registry.example?query", "https://registry.example#fragment", "https://registry.example?", "https://registry.example#"]) {
    expect(() => releaseApiUrl({ registryOrigin, producer: "TerraScale/terrabase" })).toThrow();
  }
});

test("release redirects reject missing targets before resolving a URL", () => {
  const base = "https://registry.example/releases/download/binary";
  for (const location of [null, "", "   "])
    expect(() => releaseRedirectUrl(location, base)).toThrow("nonempty Location header");
  expect(releaseRedirectUrl("/assets/binary", base)).toBe("https://registry.example/assets/binary");
  expect(releaseRedirectUrl("https://storage.example/binary", base)).toBe("https://storage.example/binary");
  expect(() => releaseRedirectUrl("http://storage.example/binary", base)).toThrow();
});

const runnerPath = fileURLToPath(new URL("./run-terrabase-integration.mjs", import.meta.url));
const withoutBinary = !process.env.TERRABASE_BINARY || process.platform !== "linux";
// The runner itself may spend 30s starting TerraBase, 10s on bootstrap fetches,
// then provisioning, and up to 20s on its concurrent stopChild cleanups. Tests
// wait on its events within this one budget instead of shorter deadlines.
const runnerBudget = 120_000;

/**
 * Resolves with the first match once the runner prints it; rejects if its
 * streams close first.
 * @param {import("node:child_process").ChildProcess} runner @param {() => string} read @param {RegExp} pattern
 * @returns {Promise<RegExpMatchArray>}
 */
function waitForOutput(runner, read, pattern) {
  return new Promise((resolve, reject) => {
    const check = () => {
      const match = read().match(pattern);
      if (match) finish(() => resolve(match));
    };
    const closed = () => finish(() => reject(new Error(`Runner exited before printing ${pattern}: ${read()}`)));
    /** @param {() => void} settle */
    const finish = settle => {
      runner.stdout?.off("data", check);
      runner.stderr?.off("data", check);
      runner.off("close", closed);
      settle();
    };
    runner.stdout?.on("data", check);
    runner.stderr?.on("data", check);
    runner.on("close", closed);
    check();
    if (runner.stdout?.readableEnded && runner.stderr?.readableEnded) closed();
  });
}

test.skipIf(withoutBinary)("pinned runner preserves a custom command with no arguments", async () => {
  const runner = spawn(process.execPath, [runnerPath, "uname"], { stdio: ["ignore", "pipe", "pipe"] });
  let log = "";
  runner.stdout.on("data", chunk => { log += chunk.toString(); });
  runner.stderr.on("data", chunk => { log += chunk.toString(); });
  try {
    const [code, signal] = await once(runner, "close");
    expect({ code, signal, log }).toMatchObject({ code: 0, signal: null });
    expect(log).toContain("Linux");
  } finally {
    await stopChild(runner);
  }
}, runnerBudget);

/** @type {NodeJS.Signals[]} */
const terminationSignals = ["SIGINT", "SIGTERM"];
test.skipIf(withoutBinary).each(terminationSignals)("pinned runner cleans children and allocation on %s", async signal => {
  const command = 'const fs = require("node:fs"); const directory = require("node:path").dirname(process.env.TERRABASE_V1_OPENAPI); process.on("SIGTERM", () => { console.log("TS498_ALLOCATION_PRESENT " + fs.existsSync(directory)); setTimeout(() => process.exit(0), 250); }); console.log("TS498_CHILD " + JSON.stringify({ pid: process.pid, parentPid: process.ppid, directory })); setInterval(() => {}, 1000);';
  const call = [process.execPath, "-e", command].map(value => "'" + value.replaceAll("'", "'\\''") + "'").join(" ");
  const runner = spawn(process.execPath, [runnerPath, "npm", "exec", "--offline", "--call", call], { stdio: ["ignore", "pipe", "pipe"] });
  let log = "";
  runner.stdout.on("data", chunk => { log += chunk.toString(); });
  runner.stderr.on("data", chunk => { log += chunk.toString(); });
  const exited = once(runner, "close");
  /** @type {number[]} */
  let children = [];
  let directory;
  try {
    const match = await waitForOutput(runner, () => log, /TS498_CHILD (\{[^\n]+\})/u);
    const target = JSON.parse(match[1] ?? "{}");
    directory = target.directory;
    children = (await readFile(`/proc/${runner.pid}/task/${runner.pid}/children`, "utf8")).trim().split(/\s+/u).map(Number);
    expect(children).not.toContain(target.pid);
    expect(children.length).toBeGreaterThanOrEqual(2);
    children.push(target.pid, target.parentPid);
    runner.kill(signal);
    expect((await waitForOutput(runner, () => log, /TS498_ALLOCATION_PRESENT (?:true|false)\n/u))[0]).toBe("TS498_ALLOCATION_PRESENT true\n");
    runner.kill(signal === "SIGINT" ? "SIGTERM" : "SIGINT");
    expect(await exited).toEqual([signal === "SIGINT" ? 130 : 143, null]);
    for (const childPid of children)
      expect(() => process.kill(childPid, 0)).toThrow();
    await expect(access(directory)).rejects.toMatchObject({ code: "ENOENT" });
  } finally {
    await stopChild(runner);
    for (const pid of children) {
      try { process.kill(pid, "SIGKILL"); } catch { /* Already reaped after successful cleanup. */ }
    }
    if (directory) await rm(directory, { recursive: true, force: true });
  }
}, runnerBudget);
