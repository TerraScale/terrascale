import type { Browser } from '@playwright/test';
import { request } from '@playwright/test';
import { createTsAuthClient } from '../../sdk/js/src/ts-auth.js';
import { auditedContext, expect } from './audit';
import { prepareAuthManagement, closeAuthManagement } from './auth-management';
import { auth, onLocalStack } from './stack';
import { journey, ownerSession, saveCookies, saveSecret } from './state';

const median = (samples: number[]) => [...samples].sort((left, right) => left - right)[Math.floor(samples.length / 2)];

export async function checkPasswordFairness(browser: Browser): Promise<void> {
  await onLocalStack(async () => {
    const current = journey();
    const context = await auditedContext(browser, { storageState: ownerSession() });
    const page = await context.newPage();
    const management = await prepareAuthManagement(page, 'hasher-fairness');
    const sdk = createTsAuthClient({ apiBaseUrl: auth, instanceSlug: 'terrascale' }).projects;
    await sdk.disableUser(management.writeKey, current.projectId, management.user);
    await sdk.enableUser(management.writeKey, current.projectId, management.user);
    const expensive = '$2b$12$abcdefghijklmnopqrstuuDcR.C7.jTI8Np2AcKw0NUM.aM/Z8Kl2';
    const password = 'Journey import password 48!';
    saveSecret('fairness-password', password);
    saveSecret('expensive-import-hash', expensive);
    const ids: string[] = [];
    const ownerHeaders = { Authorization: 'Bearer ' + management.ownerToken };
    const projectRoot = `${auth}/v1/terrascale/organizations/${current.organizationId}/projects`;
    let normalProject: string | undefined;
    const clients = await Promise.all(Array.from({ length: 9 }, () => request.newContext()));
    try {
      const project = await page.request.post(projectRoot, { headers: ownerHeaders, data: { name: 'Hasher fairness ' + current.runId } });
      expect(project.status()).toBe(201);
      normalProject = (await project.json()).id;
      const normalUser = await sdk.createUser(management.ownerToken, normalProject!, {
        email: `hasher-normal-${current.runId}@example.com`, password,
      });
      expect(normalUser.ok).toBe(true);
      if (!normalUser.ok) throw new Error('fairness user creation failed');
      const imported = await sdk.importUsers(management.writeKey, current.projectId, { users: Array.from({ length: 20 }, (_, index) => ({
        email: `hasher-${index}-${current.runId}@example.com`, password_hash: expensive,
      })) });
      expect(imported.ok).toBe(true);
      if (!imported.ok) throw new Error('fairness import failed');
      expect(imported.value.errors).toEqual([]);
      expect(imported.value.items).toHaveLength(20);
      ids.push(...imported.value.items.map(item => item.user.id));
      const normal = async () => {
        const started = performance.now();
        const response = await clients[8].post(`${auth}/ui/${normalProject}/sign-in/password`, {
          headers: { Origin: auth }, form: { email: normalUser.value.email, password },
        });
        expect(response.status()).toBe(200);
        expect(await response.text()).toContain('You are signed in.');
        return performance.now() - started;
      };
      const baseline = [];
      for (let sample = 0; sample < 3; sample++) baseline.push(await normal());
      const attempt = async (index: number, client: number) => {
        const started = performance.now();
        const response = await clients[client].post(`${auth}/ui/${current.projectId}/sign-in/password`, {
          headers: { Origin: auth }, form: { email: imported.value.items[index].user.email, password },
        });
        expect(response.status()).toBe(200);
        expect(await response.text()).toContain('You are signed in.');
        return performance.now() - started;
      };
      const singles: number[] = [];
      const bursts: number[] = [];
      for (let sample = 0; sample < 3; sample++) {
        const offset = sample * 4;
        const single = await attempt(offset, 0);
        const burst = await Promise.all([attempt(offset + 1, 1), attempt(offset + 2, 2), attempt(offset + 3, 3)]);
        singles.push(single);
        bursts.push(...burst);
        console.log(`hasher tenant sample ${sample + 1}: single ${Math.round(single)} ms, three ${burst.map(value => Math.round(value)).join(', ')} ms`);
      }
      expect(median(bursts)).toBeLessThan(median(singles) * 2);
      const loaded: number[] = [];
      for (let sample = 0; sample < 3; sample++) {
        let completed = 0;
        const flood = imported.value.items.slice(12).map((item, index) => clients[index].post(`${auth}/ui/${current.projectId}/sign-in/password`, {
          headers: { Origin: auth }, form: { email: item.user.email, password: password + ' wrong' },
        }).then(response => { completed++; return response; }));
        await Promise.race(flood);
        expect(completed).toBeLessThan(flood.length);
        const elapsed = await normal();
        loaded.push(elapsed);
        expect(completed).toBeLessThan(flood.length);
        for (const response of await Promise.all(flood)) expect(response.status()).toBe(401);
        console.log(`hasher fairness sample ${sample + 1}: baseline ${Math.round(median(baseline))} ms, other tenant ${Math.round(elapsed)} ms`);
      }
      expect(median(loaded)).toBeLessThan(median(baseline) * 2);
    } finally {
      if (normalProject) {
        const deleted = await page.request.delete(projectRoot + '/' + normalProject, { headers: ownerHeaders });
        expect(deleted.status()).toBe(204);
      }
      for (const id of ids) await sdk.deleteUser(management.writeKey, current.projectId, id);
      for (const client of clients) {
        for (const cookie of (await client.storageState()).cookies) saveSecret('fairness-cookie', cookie.value);
        await client.dispose();
      }
      await closeAuthManagement(page, management);
      await saveCookies(context);
      await context.close();
    }
  });
}
