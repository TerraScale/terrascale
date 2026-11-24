import { execFile } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { test, expect } from '../support/audit';
import { saveSecret } from '../support/state';

test('usage is metered per UTC month, persisted under the month it was recorded in, and the published allowance is enforced', async () => {
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
        TERRABASE_POSTGRES_PORT: '18622', TERRABASE_METRICS_PORT: '18623', TERRABASE_QUIC_PORT: '18624',
      },
      maxBuffer: 1024 * 1024,
    });
    expect(stdout).toContain('usage complete-month journey: history 2028-01 writes=37, absent month=0, write at boundary-1tick in 2028-02, '
      + 'read at boundary in 2028-03, invalid month=400, flushed months 2028-01,2028-02,2028-03 passed');
    expect(stdout).toContain('usage sql metering journey: before=2028-03{requests=0,reads=0,writes=0,rows_read=0,rows_written=0,strong_commits=0,storage_byte_hours=0} '
      + 'after=2028-03{requests=4,reads=1,writes=3,rows_read=3,rows_written=4,strong_commits=2,storage_byte_hours=0} refused=400 allowance hobby=1 refusal=409 database_limit');
    expect(stdout).toContain('usage organization journey: projects=2 requests=5 writes=4 reads=1 complete=True storage counted once per hour, unsampled month incomplete, refusals=400,400,400,400,400,401,403,403');
  } finally {
    await rm(data, { recursive: true, force: true });
  }
});
