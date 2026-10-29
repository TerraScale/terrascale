import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
/** The admitted five-operation consumer is the sole native qualification runner. */
const script = fileURLToPath(
  new URL("./current-producer-consumer.js", import.meta.url),
);
const child = spawn(process.execPath, [script, ...process.argv.slice(2)], {
  env: process.env,
  stdio: "inherit",
});
child.once("error", () => {
  process.exitCode = 1;
});
child.once("exit", (code) => {
  process.exitCode = code ?? 1;
});
