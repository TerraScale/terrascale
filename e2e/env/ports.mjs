import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(new URL('./ports.py', import.meta.url));
const holders = new Set();
process.on('exit', () => {
  for (const child of holders) child.kill('SIGTERM');
});

export function readPorts(file = process.env.E2E_PORTS_FILE ?? join(process.env.E2E_STATE ?? '', 'ports.json')) {
  return JSON.parse(readFileSync(file, 'utf8'));
}

export function port(name, file) {
  const value = readPorts(file)[name];
  if (!Number.isInteger(value)) throw new Error('missing e2e port ' + name);
  return value;
}

export function holdSync(preferred, mode = 'flex') {
  const directory = mkdtempSync(join(tmpdir(), 'e2e-ports-'));
  const out = join(directory, 'ports.json');
  const args = [script, 'hold', '--out', out];
  if (mode === 'block') args.push('--block');
  if (mode === 'exact') args.push('--exact');
  for (const [name, value] of Object.entries(preferred)) args.push(name + '=' + value);
  const child = spawn('python3', args, { stdio: ['ignore', 'pipe', 'inherit'] });
  holders.add(child);
  const started = Date.now();
  while (Date.now() - started < 10_000) {
    try {
      const ports = JSON.parse(readFileSync(out, 'utf8'));
      return { ports, stop() { child.kill('SIGTERM'); holders.delete(child); } };
    } catch (error) {
      if (error.code !== 'ENOENT' && !(error instanceof SyntaxError)) throw error;
      if (child.exitCode !== null) throw new Error('e2e port registry exited ' + child.exitCode);
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 20);
    }
  }
  child.kill('SIGTERM');
  throw new Error('e2e port registry did not assign ports');
}
