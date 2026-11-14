import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { stateDir } from './stack';

export async function delayCommitSync(wal: string) {
  const directory = mkdtempSync(join(stateDir, 'commit-sync-'));
  const trace = join(directory, 'trace');
  const pid = readFileSync(join(stateDir, 'pids', 'terrabase.pid'), 'utf8').trim();
  const child = spawn('strace', ['--interruptible=1', '-ff', '-yy', '-ttt', '-T', '-p', pid, '-e', 'trace=fsync', '-P', wal,
    '-e', 'inject=fsync:delay_enter=2s:when=1', '-o', trace], { stdio: ['ignore', 'ignore', 'pipe'] });
  const exited = new Promise<void>(resolve => child.once('exit', () => resolve()));
  let stderr = '';
  try {
    await new Promise<void>((resolve, reject) => {
      child.once('error', reject);
      child.once('exit', () => reject(new Error('native commit tracing ended before attachment: ' + stderr)));
      child.stderr.on('data', chunk => {
        stderr += chunk.toString();
        if (stderr.includes(' attached')) resolve();
      });
    });
  } catch (error) {
    if (child.pid) {
      child.kill('SIGINT');
      await exited;
    }
    rmSync(directory, { recursive: true });
    throw error;
  }

  let finishing: Promise<string> | undefined;
  return {
    finish: () => finishing ??= (async () => {
      child.kill('SIGINT');
      await exited;
      try {
        return readdirSync(directory).filter(file => file.startsWith('trace.')).map(file => readFileSync(join(directory, file), 'utf8')).join('\n');
      } finally {
        rmSync(directory, { recursive: true });
      }
    })(),
  };
}
