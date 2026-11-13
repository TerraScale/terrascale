import { createHmac, hkdfSync } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { authRows } from './auth-storage';
import { stateDir } from './stack';

export async function signupCount(address: string, tenant = 'terrascale'): Promise<number> {
  const master = Buffer.from(readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[2], 'base64');
  const foldKey = Buffer.from(hkdfSync('sha256', master, Buffer.alloc(0), 'ts-auth rate-limit folds', 32));
  const idKey = Buffer.from(hkdfSync('sha256', master, Buffer.alloc(0), 'ts-auth rate-limit bucket ids', 32));
  const subject = 'fold-' + createHmac('sha256', foldKey).update(address).digest().readUInt16BE();
  const keys = ['gru', 'sjc', 'fra'].map(region => createHmac('sha256', idKey)
    .update(`hosted-signup-ip/3600s:${region}:${subject}`).digest('hex'));
  const rows = await authRows('SELECT COALESCE(SUM(used), 0) FROM rate_limits WHERE instance_id = ? AND key IN (?, ?, ?)', [tenant, ...keys]);
  return Number(rows[0][0]);
}

export async function reportSignupCounts(): Promise<void> {
  const run = process.env.E2E_CLIENT_RUN;
  const entries = readFileSync(join(stateDir, `client-addresses-${run}.jsonl`), 'utf8').split('\n').filter(Boolean)
    .map(line => JSON.parse(line) as { spec: string; address: string });
  const addresses = new Map(entries.map(entry => [entry.address, entry.spec]));
  for (const [address, spec] of addresses) {
    console.log(`signup address ${address} ${spec}: ${await signupCount(address)}/20 per hour`);
  }
}
