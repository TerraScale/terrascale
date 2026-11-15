import { spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { e2eRoot } from './stack';

const script = join(e2eRoot, 'env', 'ports.py');
const holders = new Set<ChildProcess>();
process.on('exit', () => {
  for (const child of holders) child.kill('SIGTERM');
});

export function holdSync(preferred: Record<string, number>, mode: 'flex' | 'block' | 'exact' = 'flex'): { ports: Record<string, number>; stop(): void } {
  const directory = mkdtempSync(join(tmpdir(), 'e2e-ports-'));
  const out = join(directory, 'ports.json');
  const args = [script, 'hold', '--out', out];
  if (mode === 'block') args.push('--block');
  if (mode === 'exact') args.push('--exact');
  for (const [name, value] of Object.entries(preferred)) args.push(name + '=' + value);
  const child = spawn('python3', args, { stdio: ['ignore', 'pipe', 'inherit'] });
  child.unref();
  (child.stdout as { unref(): void } | null)?.unref();
  holders.add(child);
  const started = Date.now();
  while (Date.now() - started < 10_000) {
    try {
      const ports = JSON.parse(readFileSync(out, 'utf8')) as Record<string, number>;
      return { ports, stop() { child.kill('SIGTERM'); holders.delete(child); } };
    } catch (error) {
      const code = (error as { code?: string }).code;
      if (code !== 'ENOENT' && !(error instanceof SyntaxError)) throw error;
      if (child.exitCode !== null) throw new Error('e2e port registry exited ' + child.exitCode);
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 20);
    }
  }
  child.kill('SIGTERM');
  throw new Error('e2e port registry did not assign ports');
}
