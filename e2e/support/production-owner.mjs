import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

function alive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

function futureCooldowns(state) {
  const dir = join(state, 'inbox-cooldowns');
  if (!existsSync(dir)) return [];
  return readdirSync(dir).filter((name) => /^\d+$/.test(name) && Number(name) > Date.now());
}

export function acquireOwner(state, pid = process.pid) {
  mkdirSync(join(state, '..'), { recursive: true });
  const lock = `${state}.owner`;
  const claim = () => {
    mkdirSync(lock);
    writeFileSync(join(lock, 'pid'), String(pid));
  };
  try {
    claim();
  } catch (error) {
    if (error.code !== 'EEXIST') throw error;
    const pid = Number(readFileSync(join(lock, 'pid'), 'utf8'));
    if (alive(pid)) throw new Error('production state is owned by another run');
    const stale = `${lock}.stale.${process.pid}`;
    try {
      renameSync(lock, stale);
    } catch {
      throw new Error('production state is owned by another run');
    }
    rmSync(stale, { recursive: true, force: true });
    claim();
  }
  const kept = futureCooldowns(state);
  rmSync(state, { recursive: true, force: true });
  mkdirSync(state, { recursive: true });
  if (!kept.length) return;
  const dir = join(state, 'inbox-cooldowns');
  mkdirSync(dir);
  for (const name of kept) writeFileSync(join(dir, name), '');
}

export function releaseOwner(state, pid = process.pid) {
  const lock = `${state}.owner`;
  const pidFile = join(lock, 'pid');
  if (!existsSync(pidFile)) return;
  if (Number(readFileSync(pidFile, 'utf8')) !== Number(pid)) return;
  rmSync(lock, { recursive: true, force: true });
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const state = process.argv[3];
  const owner = process.argv[4] ? Number(process.argv[4]) : process.pid;
  if (process.argv[2] === 'acquire') acquireOwner(state, owner);
  else if (process.argv[2] === 'release') releaseOwner(state, owner);
  else process.exit(2);
}
