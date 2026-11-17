import { test, expect } from '../support/audit';
import { authenticatedCells } from '../placement/support';
import { request as clientRequest } from '../support/client-address';
import { journey } from '../support/state';

test('request logs append live and a selected status filter stays still', async ({ page, request, audit }) => {
  const current = journey();
  const cells = await authenticatedCells(clientRequest, request, current.apiKey, current.projectId);
  const path = `/databases/${current.databaseId}/logs`;
  const endpoint = `https://api.terrascale.tech/v1/projects/${current.projectId}/databases/${current.databaseId}/sql/execute`;
  try {
    await page.goto(path);
    const url = page.url();
    await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'live-logs'));
    const generated = await cells.post(endpoint, { data: { statements: [{ sql: 'SELECT 73' }] } });
    expect(generated.status()).toBe(200);
    const requestId = generated.headers()['x-request-id'];
    expect(requestId).toBeTruthy();
    await expect(page.locator('#request-log-results')).toContainText(requestId);
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'live-logs');
    await expect(page).toHaveURL(url);
    await page.locator('#request-status').selectOption('2xx');
    await expect(page.locator('#request-log-results[data-init]')).toHaveCount(0);
    const frozen = await page.locator('#request-log-results').innerHTML();
    const later = await cells.post(endpoint, { data: { statements: [{ sql: 'SELECT 74' }] } });
    expect(later.status()).toBe(200);
    await expect.poll(async () => {
      const response = await cells.get(endpoint.replace('/sql/execute', '/logs'));
      expect(response.status()).toBe(200);
      return (await response.text()).includes(later.headers()['x-request-id']);
    }).toBe(true);
    await expect(page.locator('#request-log-results')).toHaveJSProperty('innerHTML', frozen);
    await audit.check('live request logs and stable filtered results');
  } finally {
    await cells.dispose();
  }
});

