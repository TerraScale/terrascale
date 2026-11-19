import { spawn, type ChildProcess } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, mkdirSync, mkdtempSync, openSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { holdSync } from '../support/ports';
import { mailpit, production, stateDir } from '../support/stack';
import { saveSecret } from '../support/state';

test.skip(production, 'the staging identity runs throwaway services');
test.describe.configure({ mode: 'serial' });

const ports = holdSync({ client: 18680, internal: 18681, postgres: 18682, metrics: 18683, auth: 18690, authMetrics: 18692 }, 'block').ports;
const stagingOrigin = 'https://stg.auth.terrascale.tech';
const stagingIssuer = stagingOrigin + '/terrascale';
const stagingAudience = 'https://stg.api.terrascale.tech';
const productionIssuer = 'https://auth.terrascale.tech/terrascale';
const terrabase = join(process.env.E2E_BUILD!, 'terrabase', 'terrabase.dll');
const tsAuth = join(process.env.E2E_BUILD!, 'ts-auth', 'ts-auth.dll');
const crockford = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

mkdirSync(stateDir, { recursive: true });
const directory = mkdtempSync(join(stateDir, 'staging-'));
const [r2Key, r2Secret] = readFileSync(join(stateDir, 'r2'), 'utf8').split('\n');
const secret = (name: string, value: string): string => {
  saveSecret('staging-' + name, value);
  return value;
};
const peer = secret('peer', randomBytes(32).toString('hex'));
const serviceClient = '0' + [...randomBytes(25)].map((byte) => crockford[byte % 32]).join('');
const serviceSecret = secret('service-client', randomBytes(36).toString('base64url'));
const clean = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
  !/^(TERRABASE_|TS_|AWS_|CLOUDFLARE_)/.test(name) && name !== 'BUCKET_NAME'));

const stagingTerraBase: Record<string, string> = {
  DOTNET_ENVIRONMENT: 'Production',
  TERRABASE_DATA_DIRECTORY: join(directory, 'terrabase'),
  TERRABASE_PORT: String(ports.client),
  TERRABASE_INTERNAL_PORT: String(ports.internal),
  TERRABASE_POSTGRES_PORT: String(ports.postgres),
  TERRABASE_METRICS_PORT: String(ports.metrics),
  TERRABASE_MEMORY_BUDGET: '67108864',
  TERRABASE_STORAGE_THREADS: '1',
  TERRABASE_AUTH_ISSUER: stagingIssuer,
  TERRABASE_AUTH_AUDIENCE: stagingAudience,
  TERRABASE_PEER_SECRET: peer,
  TERRABASE_MASTER_KEY: secret('terrabase-master-key', randomBytes(32).toString('base64')),
  AWS_ENDPOINT_URL_S3: 'https://journey.backups.terrascale.tech/',
  AWS_REGION: 'auto',
  BUCKET_NAME: 'journey-stg-backups',
  AWS_ACCESS_KEY_ID: r2Key,
  AWS_SECRET_ACCESS_KEY: r2Secret,
  TERRABASE_SHAPES_ACCESS_KEY_ID: r2Key,
  TERRABASE_SHAPES_SECRET_ACCESS_KEY: r2Secret,
  TERRABASE_SHAPES_CDN_API_TOKEN: secret('shapes-cdn-token', randomBytes(24).toString('hex')),
  TERRABASE_SHAPES_BUCKET: 'journey-stg-shapes',
  TERRABASE_SHAPES_CDN_HOST: 'shapes.journey-stg.example',
  TERRABASE_SHAPES_CDN_ZONE_ID: randomBytes(16).toString('hex'),
};

const stagingAuth: Record<string, string> = {
  TS_AUTH_PUBLIC_ORIGIN: stagingOrigin,
  TS_AUTH_ACCESS_TOKEN_AUDIENCES: stagingAudience,
  TS_AUTH_LISTEN: 'http://127.0.0.1:' + ports.auth,
  TS_AUTH_METRICS_LISTEN: 'http://127.0.0.1:' + ports.authMetrics,
  TS_AUTH_TERRABASE_URL: 'http://127.0.0.1:' + ports.internal,
  TERRABASE_PEER_SECRET: peer,
  TS_AUTH_MASTER_KEY: secret('auth-master-key', randomBytes(32).toString('base64')),
  TS_AUTH_AUDIT_SIGNING_KEY: secret('auth-audit-signing-key', randomBytes(32).toString('base64')),
  TS_AUTH_DASHBOARD_CLIENT_SECRET: secret('dashboard-client', randomBytes(27).toString('base64url')),
  TS_AUTH_DASHBOARD_REDIRECT_URIS: 'https://stg.dash.terrascale.tech/login/callback',
  TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS: 'https://stg.dash.terrascale.tech/login',
  TS_AUTH_SERVICE_CLIENTS: serviceClient + ':' + serviceSecret,
  TS_AUTH_TURNSTILE_SITE_KEY: '1x00000000000000000000AA',
  TS_AUTH_TURNSTILE_SECRET_KEY: '1x0000000000000000000000000000000AA',
  TS_REGION: 'stg',
  TS_NODE: 'journey-stg',
};

const email: Record<string, string> = {
  TS_AUTH_SMTP_HOST: 'smtp.email.sa-saopaulo-1.oci.oraclecloud.com',
  TS_AUTH_SMTP_USERNAME: randomBytes(8).toString('hex'),
  TS_AUTH_SMTP_PASSWORD: secret('smtp-password', randomBytes(24).toString('base64url')),
  CLOUDFLARE_ACCOUNT_ID: randomBytes(16).toString('hex'),
  CLOUDFLARE_EMAIL_TOKEN: secret('cloudflare-email-token', randomBytes(24).toString('base64url')),
};

const running = new Set<ChildProcess>();
let launches = 0;

function launch(binary: string, environment: Record<string, string>, args: string[] = []): { child: ChildProcess; log: string } {
  const log = join(directory, `process-${++launches}.log`);
  const output = openSync(log, 'w', 0o600);
  const child = spawn('dotnet', [binary, ...args], {
    env: { ...clean, DOTNET_ROOT: process.env.DOTNET_ROOT, DOTNET_PROCESSOR_COUNT: '2', ...environment },
    stdio: ['ignore', output, output],
  });
  closeSync(output);
  running.add(child);
  return { child, log };
}

async function exited(binary: string, environment: Record<string, string>, args: string[] = []): Promise<{ code: number | null; output: string }> {
  const { child, log } = launch(binary, environment, args);
  const [code] = await once(child, 'exit');
  running.delete(child);
  return { code: code as number | null, output: readFileSync(log, 'utf8') };
}

async function refused(binary: string, environment: Record<string, string>, message: string): Promise<void> {
  const result = await exited(binary, environment);
  expect(result.output).toContain(message);
  expect(result.code).toBe(2);
}

async function ready(child: ChildProcess, url: string): Promise<void> {
  await expect.poll(async () => {
    expect(child.exitCode, 'the staging service stays running').toBeNull();
    return (await fetch(url).catch(() => null))?.status ?? 0;
  }, { timeout: 90_000 }).toBe(200);
}

function counter(metrics: string, name: string, outcome: string): number {
  const line = metrics.split('\n').find((entry) => entry.startsWith(name + '{') && entry.includes(`outcome="${outcome}"`));
  return line ? Number(line.slice(line.lastIndexOf(' ') + 1)) : 0;
}

test.afterAll(async () => {
  for (const child of running) {
    if (child.exitCode === null && child.signalCode === null) {
      const stopped = once(child, 'exit');
      child.kill('SIGTERM');
      await stopped;
    }
  }
  running.clear();
});

test('TerraBase refuses a shape identity that crosses environments', async () => {
  const unlocated = Object.fromEntries(Object.entries(stagingTerraBase).filter(([name]) =>
    !['TERRABASE_SHAPES_BUCKET', 'TERRABASE_SHAPES_CDN_HOST', 'TERRABASE_SHAPES_CDN_ZONE_ID'].includes(name)));
  await refused(terrabase, unlocated, "production's shape storage and CDN zone serve only the production auth-issuer");
  await refused(terrabase, { ...stagingTerraBase, TERRABASE_SHAPES_CDN_ZONE_ID: '4cc466e19478c116fae7dc445a6d0911' },
    "must name production's shape storage entirely or share none of it");
  await refused(terrabase, { ...stagingTerraBase, TERRABASE_AUTH_ISSUER: productionIssuer, TERRABASE_AUTH_AUDIENCE: 'https://api.terrascale.tech' },
    "the production auth-issuer uses only production's shape storage and CDN zone");
  await refused(terrabase, { ...stagingTerraBase, TERRABASE_SHAPES_CDN_ZONE_ID: 'not-a-zone' }, 'shapes-cdn-zone-id must be a Cloudflare zone id');
  await refused(terrabase, { ...stagingTerraBase, TERRABASE_SHAPES_CDN_HOST: 'shapes-terrascale-stg-terrabase-shapes.terrascale.tech' },
    "another environment's CDN host must lie outside production's zone");
});

test('ts-auth refuses the wrong authority, email in staging and production without email', async () => {
  await refused(tsAuth, { ...stagingAuth, TS_REGION: 'gru' }, 'The staging ts-auth runs only in its authority region stg');
  await refused(tsAuth, { ...stagingAuth, TS_AUTH_PUBLIC_ORIGIN: 'https://auth.terrascale.tech', ...email },
    'The production ts-auth runs only in its authority region gru');
  await refused(tsAuth, { ...stagingAuth, TS_AUTH_PUBLIC_ORIGIN: 'https://auth.terrascale.tech', TS_REGION: 'gru' }, 'are required in production');
  await refused(tsAuth, { ...stagingAuth, ...email }, 'Staging sends no email');
  await refused(tsAuth, { ...stagingAuth, TS_AUTH_PUBLIC_ORIGIN: 'https://preview.auth.terrascale.tech' },
    'TS_AUTH_PUBLIC_ORIGIN must be https://auth.terrascale.tech or https://stg.auth.terrascale.tech outside Development');
});

test('a staging cell serves its own issuer and audience from its own storage and sends no email', async () => {
  const cell = launch(terrabase, stagingTerraBase);
  await ready(cell.child, 'http://127.0.0.1:' + ports.client + '/readyz');
  const service = launch(tsAuth, stagingAuth);
  const provisioned = await exited(tsAuth, stagingAuth, ['provision-signing-key']);
  expect(provisioned.code, provisioned.output).toBe(0);
  const authOrigin = 'http://127.0.0.1:' + ports.auth;
  await ready(service.child, authOrigin + '/readyz');

  const discovery = await (await fetch(authOrigin + '/terrascale/.well-known/openid-configuration')).json() as { issuer: string };
  expect(discovery.issuer).toBe(stagingIssuer);

  const token = await fetch(authOrigin + '/terrascale/oauth/token', {
    method: 'POST',
    body: new URLSearchParams({ grant_type: 'client_credentials', client_id: serviceClient, client_secret: serviceSecret }),
  });
  expect(token.status).toBe(200);
  const accessToken = secret('access-token', ((await token.json()) as { access_token: string }).access_token);
  const claims = JSON.parse(Buffer.from(accessToken.split('.')[1], 'base64url').toString()) as { iss: string; aud: string | string[] };
  expect(claims.iss).toBe(stagingIssuer);
  expect([claims.aud].flat()).toContain(stagingAudience);

  const address = `staging-${randomBytes(6).toString('hex')}@journey.terrascale.tech`;
  const signUp = await fetch(authOrigin + '/ui/terrascale/sign-up', {
    method: 'POST',
    headers: { origin: stagingOrigin },
    body: new URLSearchParams({
      email: address,
      password: secret('sign-up-password', randomBytes(18).toString('base64url')),
      'cf-turnstile-response': ['XXXX.DUMMY.TOKEN.XXXX', 'stg.auth.terrascale.tech', 'sign_up', 'terrascale'].join('|'),
    }),
  });
  expect(signUp.status).toBe(200);
  expect(await signUp.text()).toContain('name="pending_token"');
  const metricsUrl = 'http://127.0.0.1:' + ports.authMetrics + '/metrics';
  await expect.poll(async () => counter(await (await fetch(metricsUrl)).text(), 'ts_auth_emails_total', 'not_configured'),
    { timeout: 30_000 }).toBeGreaterThan(0);
  expect(counter(await (await fetch(metricsUrl)).text(), 'ts_auth_emails_total', 'sent')).toBe(0);
  const inbox = await (await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${address}"`)}`)).json() as { messages: unknown[] };
  expect(inbox.messages).toHaveLength(0);
});
