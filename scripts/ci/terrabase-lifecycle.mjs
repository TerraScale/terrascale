import { setTimeout as delay } from "node:timers/promises";
import { readFile, readdir } from "node:fs/promises";

/** @param {{ log: string, started: boolean, admin: string, clientPort: string, pending: Record<"stdout" | "stderr", string> }} state @param {Buffer | string} chunk @param {"stdout" | "stderr"} [stream] */
export function captureProducerOutput(state, chunk, stream = "stdout") {
  const text = chunk.toString();
  const input = state.pending[stream] + text;
  const end = input.lastIndexOf("\n") + 1;
  const output = input.slice(0, end);
  state.admin ||= output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)(?=\s)/u)?.[1] ?? "";
  state.clientPort ||= output.match(/Now listening on: https:\/\/[^\s]+:(\d+)(?=\s)/u)?.[1] ?? "";
  state.started ||= output.includes("Application started.");
  state.pending[stream] = input.slice(end).slice(-64 * 1024);
  state.log = (state.log + text).slice(-64 * 1024);
}

/** @param {number} pid */
async function hasRunningGroup(pid) {
  // Container PID 1 may leave zombies unreaped; they cannot use the allocation.
  const entries = await readdir("/proc");
  const running = await Promise.all(entries.filter(name => /^\d+$/u.test(name)).map(async name => {
    try {
      const stat = await readFile(`/proc/${name}/stat`, "utf8");
      // comm can contain spaces and parentheses; the final ')' ends that field.
      const [state, , group] = stat.slice(stat.lastIndexOf(")") + 2).trim().split(/\s+/u);
      return Number(group) === pid && state !== "Z" && state !== "X" && state !== "x";
    } catch (error) {
      if (!["ENOENT", "ESRCH"].includes(/** @type {NodeJS.ErrnoException} */ (error).code ?? "")) throw error;
      return false;
    }
  }));
  return running.some(Boolean);
}

/** @param {import("node:child_process").ChildProcess | undefined} child @param {boolean} [processGroup] */
export async function stopChild(child, processGroup = false) {
  const pid = child?.pid;
  if (!pid || (!processGroup && (child.exitCode !== null || child.signalCode !== null))) return;
  /** @param {NodeJS.Signals | 0} signal */
  const send = signal => {
    try {
      return processGroup ? process.kill(-pid, signal) : child.kill(signal);
    } catch (error) {
      if (/** @type {NodeJS.ErrnoException} */ (error).code !== "ESRCH") throw error;
      return false;
    }
  };
  send("SIGTERM");
  const deadline = Date.now() + 15_000;
  while ((child.exitCode === null && child.signalCode === null) ||
    (processGroup && send(0) && (process.platform !== "linux" || await hasRunningGroup(pid)))) {
    if (Date.now() >= deadline) send("SIGKILL");
    if (Date.now() >= deadline + 5000) throw new Error("Child process group did not terminate; allocation retained.");
    await delay(25);
  }
}
