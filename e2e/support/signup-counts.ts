import { createHmac, hkdfSync } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { authRows } from './auth-storage';
import { stateDir } from './stack';
import type { SetupJourney } from './state';

function derived(info: string): Buffer {
  const master = Buffer.from(readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[2], 'base64');
  return Buffer.from(hkdfSync('sha256', master, Buffer.alloc(0), info, 32));
}

async function bucketUse(address: string, subject: string, instance: string): Promise<number> {
  const idKey = derived('ts-auth rate-limit bucket ids');
  const keys = ['gru', 'sjc', 'fra'].map(region => createHmac('sha256', idKey).update(`${address}:${region}:${subject}`).digest('hex'));
  const rows = await authRows('SELECT COALESCE(SUM(used), 0) FROM rate_limits WHERE instance_id = ? AND key IN (?, ?, ?)', [instance, ...keys]);
  return Number(rows[0][0]);
}

export async function signupCount(address: string, tenant = 'terrascale'): Promise<number> {
  const subject = 'fold-' + createHmac('sha256', derived('ts-auth rate-limit folds')).update(address).digest().readUInt16BE();
  return bucketUse('hosted-signup-ip/3600s', subject, tenant);
}

export async function apiKeyCreations(userId: string): Promise<number> {
  return bucketUse('api-key-create/3600s', userId, 'terrascale');
}

export async function reportSignupCounts(): Promise<void> {
  const run = process.env.E2E_CLIENT_RUN;
  const entries = readFileSync(join(stateDir, `client-addresses-${run}.jsonl`), 'utf8').split('\n').filter(Boolean)
    .map(line => JSON.parse(line) as { spec: string; address: string });
  const addresses = new Map(entries.map(entry => [entry.address, entry.spec]));
  for (const [address, spec] of addresses) {
    console.log(`signup address ${address} ${spec}: ${await signupCount(address)}/20 per hour`);
  }
  for (const file of readdirSync(stateDir).filter(name => name.endsWith('.journey.json'))) {
    const owner = JSON.parse(readFileSync(join(stateDir, file), 'utf8')) as Partial<SetupJourney>;
    if (owner.userId) console.log(`API key creations ${file.replace('.journey.json', '')} owner: ${await apiKeyCreations(owner.userId)}/20 per hour`);
  }
}
