import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createInterface } from 'node:readline';
import type { Page } from '@playwright/test';
import { verifyAgentToken } from '../../sdk/js/src/ts-auth.js';
import { expect, type Audit } from '../support/audit';
import { saveSecret } from '../support/state';
import { stateDir } from '../support/stack';
import { approveAgentEnrollment } from './agent-identities';

export async function cliAgentEnrollment(page: Page, audit: Audit, project: string, credential: string, owner: string, password: string, readCredential: string): Promise<void> {
  const config = await mkdtemp(join(tmpdir(), 'ts-agent-'));
  const binary = join(process.env.E2E_SDK_BUILD ?? join(stateDir, 'sdk'), 'go', 'ts');
  const env = { ...process.env, XDG_CONFIG_HOME: config, TERRASCALE_API_KEY: credential };
  const command = async (...args: string[]): Promise<string> => {
    const child = spawn(binary, ['agent', ...args, '--project', project], { env: { ...env, TERRASCALE_API_KEY: args[0] === 'list' || args[0] === 'policy' && args[1] === 'get' ? readCredential : credential }, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '';
    child.stdout.on('data', chunk => { output += chunk; });
    child.stderr.resume();
    await new Promise<void>((resolve, reject) => {
      child.once('error', () => reject(new Error('Agent CLI could not start')));
      child.once('close', code => code === 0 ? resolve() : reject(new Error('Agent CLI command failed: ' + args.slice(0, args[0] === 'policy' ? 2 : 1).join(' '))));
    });
    return output;
  };
  const child = spawn(binary, ['agent', 'enroll', '--project', project, '--name', 'CLI agent', '--scope', 'invoices:read'], { env, stdio: ['ignore', 'pipe', 'pipe'] });
  const exited = new Promise<void>((resolve, reject) => {
    child.once('error', () => reject(new Error('Agent CLI could not start')));
    child.once('close', code => code === 0 ? resolve() : reject(new Error('Agent CLI enrollment failed')));
  });
  let identity = '';
  child.stdout.on('data', chunk => { identity += chunk; });
  const enrollment = new Promise<{ userCode: string, verificationUriComplete: string, fingerprint: string }>((resolve, reject) => {
    void exited.then(() => reject(new Error('Agent CLI exited before enrollment was shown')), reject);
    const data = { userCode: '', verificationUriComplete: '', fingerprint: '' };
    const lines = createInterface({ input: child.stderr });
    lines.on('line', line => {
      if (line.startsWith('Code: ')) data.userCode = line.slice(6);
      if (line.startsWith('Open ')) data.verificationUriComplete = line.slice(5);
      if (line.startsWith('Key fingerprint: ')) { data.fingerprint = line.slice(17); resolve(data); }
    });
  });
  try {
    await approveAgentEnrollment(page, audit, await enrollment, password);
    await exited;
    const agent = JSON.parse(identity) as { agent_id: string, kid: string };
    const path = join(config, 'terrascale', 'agent-' + project + '-' + agent.agent_id + '.json');
    expect((await stat(path)).mode & 0o777).toBe(0o600);
    expect((await stat(join(config, 'terrascale'))).mode & 0o777).toBe(0o700);
    const stored = JSON.parse(await readFile(path, 'utf8')) as { key: string };
    saveSecret('cli-agent-private-key', stored.key);
    const token = (await command('token', '--agent', agent.agent_id)).trim();
    saveSecret('cli-agent-token', token);
    const verified = await verifyAgentToken(token, { project });
    expect(verified.ok && verified.value).toEqual({ isAgent: true, agentId: agent.agent_id, ownerId: owner, actingFor: 'owner', scopes: ['invoices:read'] });
    const listed = JSON.parse(await command('list')) as { items: { id: string }[] };
    expect(listed.items.some(item => item.id === agent.agent_id)).toBe(true);
    const policy = JSON.parse(await command('policy', 'get')) as { allowed_scopes: string[] };
    const set = JSON.parse(await command('policy', 'set', ...policy.allowed_scopes.flatMap(scope => ['--scope', scope]))) as { allowed_scopes: string[] };
    expect(set.allowed_scopes).toEqual(policy.allowed_scopes);
    await command('revoke', '--agent', agent.agent_id);
    await expect(command('token', '--agent', agent.agent_id)).rejects.toThrow('Agent CLI command failed');
  } finally {
    child.kill();
    await exited.catch(() => undefined);
    await rm(config, { recursive: true, force: true });
  }
}
