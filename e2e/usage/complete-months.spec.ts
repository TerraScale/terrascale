import { execFile } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { test, expect } from '../support/audit';
import { saveSecret } from '../support/state';

test('the allowance endpoint reads complete persisted months without inventing zero usage or finality', async () => {
  const build = process.env.E2E_BUILD;
  if (!build) throw new Error('E2E_BUILD is required for the usage journey');
  const data = await mkdtemp(join(tmpdir(), 'usage-months-'));
  const master = randomBytes(32).toString('base64');
  const peer = randomBytes(32).toString('hex');
  saveSecret('usage-months-master', master);
  saveSecret('usage-months-peer', peer);
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('TERRABASE_') && !key.startsWith('TS_')));
  try {
    const { stdout } = await promisify(execFile)('dotnet', [join(build, 'usage', 'terrabase.dll')], {
      env: {
        ...env, DOTNET_ENVIRONMENT: 'Development', TERRABASE_DATA_DIRECTORY: data,
        TERRABASE_MASTER_KEY: master, TERRABASE_PEER_SECRET: peer,
        TERRABASE_PORT: '18620', TERRABASE_INTERNAL_PORT: '18621',
        TERRABASE_POSTGRES_PORT: '18622', TERRABASE_METRICS_PORT: '18623',
      },
      maxBuffer: 1024 * 1024,
    });
    expect(stdout).toContain('usage complete-month journey: two months back=37, persisted zero=0, absent=null, paging=25+2, current/future=400, UTC boundary and Usage-Final=false passed');
  } finally {
    await rm(data, { recursive: true, force: true });
  }
});
