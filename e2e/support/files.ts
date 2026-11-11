import { createHash, createHmac } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { r2StandIn, stateDir } from './stack';

export type LeftFiles = { bucket: string; live: string };
export type Reservation = { id: string; path: string; url: string; headers: Record<string, string> };

const leftFile = join(stateDir, 'files-left.json');
const sha256 = (data: string | Buffer) => createHash('sha256').update(data);
const hmac = (key: string | Buffer, data: string) => createHmac('sha256', key).update(data).digest();

export function saveLeftFiles(left: LeftFiles): void {
  writeFileSync(leftFile, JSON.stringify(left), { mode: 0o600 });
}

export function leftFiles(): LeftFiles {
  if (!existsSync(leftFile)) throw new Error('interactions/files.spec.ts left no bucket for the account deletion');
  return JSON.parse(readFileSync(leftFile, 'utf8')) as LeftFiles;
}

export const objectPath = (url: string) => new URL(url).pathname;

export async function csrfToken(page: Page): Promise<string> {
  return page.locator('input[name=_csrf]').first().inputValue();
}

export async function reserveUpload(page: Page, bucket: string, name: string, contentType: string, content: Buffer): Promise<Reservation> {
  const response = await page.request.post('/api/v1/dashboard/storage/upload', {
    headers: { Accept: 'application/json' },
    form: { _csrf: await csrfToken(page), bucket, metadata: '{}', name, contentType, size: String(content.length), sha256: sha256(content).digest('base64') },
  });
  if (response.status() !== 200) throw new Error(`reserving an upload answered ${response.status()}`);
  const signed = (await response.json()) as { id: string; url: string; headers: Record<string, string> };
  return { id: signed.id, path: objectPath(signed.url), url: signed.url, headers: signed.headers };
}

export async function completeUpload(page: Page, bucket: string, fileId: string): Promise<number> {
  return (await page.request.post('/api/v1/dashboard/storage/complete', { form: { _csrf: await csrfToken(page), bucket, fileId } })).status();
}

export async function deleteFile(page: Page, bucket: string, fileId: string): Promise<number> {
  return (await page.request.post('/api/v1/dashboard/storage/delete', { form: { _csrf: await csrfToken(page), bucket, fileId }, maxRedirects: 0 })).status();
}

export async function putReserved(reservation: Reservation, content: Buffer): Promise<number> {
  const headers = Object.fromEntries(Object.entries(reservation.headers).filter(([name]) => name.toLowerCase() !== 'content-length'));
  return (await fetch(reservation.url, { method: 'PUT', headers, body: new Uint8Array(content) })).status;
}

export async function stored(path: string): Promise<boolean> {
  const [id, secret] = readFileSync(join(stateDir, 'r2'), 'utf8').split('\n');
  const host = new URL(r2StandIn).host;
  const date = new Date().toISOString().replace(/[-:]|\.\d{3}/g, '');
  const scope = `${date.slice(0, 8)}/auto/s3/aws4_request`;
  const payload = sha256('').digest('hex');
  const signedHeaders = 'host;x-amz-content-sha256;x-amz-date';
  const canonical = ['HEAD', path, '', `host:${host}\nx-amz-content-sha256:${payload}\nx-amz-date:${date}\n`, signedHeaders, payload].join('\n');
  const key = scope.split('/').reduce<string | Buffer>((derived, part) => hmac(derived, part), `AWS4${secret}`);
  const signature = hmac(key, ['AWS4-HMAC-SHA256', date, scope, sha256(canonical).digest('hex')].join('\n')).toString('hex');
  const response = await fetch(`${r2StandIn}${path}`, {
    method: 'HEAD',
    headers: { 'x-amz-date': date, 'x-amz-content-sha256': payload, authorization: `AWS4-HMAC-SHA256 Credential=${id}/${scope}, SignedHeaders=${signedHeaders}, Signature=${signature}` },
  });
  if (response.status !== 200 && response.status !== 404) throw new Error(`the object store answered ${response.status} to a signed HEAD`);
  return response.status === 200;
}
