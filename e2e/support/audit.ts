import AxeBuilder from '@axe-core/playwright';
import { expect, test as base, type Browser, type BrowserContext, type BrowserContextOptions, type Page, type Request, type Response, type TestInfo } from '@playwright/test';
import { checkLinks, type Link } from './links';
import { stackHosts } from './stack';

const wcag = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
const secrets = 'input[type=password], .ts-apikeys-secret-value, #access-token-secret';

type Expected = { url: RegExp; status: number; seen: number; logged: number };

export class Audit {
  private problems: string[] = [];
  private readonly expected: Expected[] = [];
  private readonly inflight = new Map<Request, number>();
  private generation = 0;
  private lastActivity = Date.now();

  constructor(
    private readonly page: Page,
    private readonly info: TestInfo,
  ) {
    page.on('console', (message) => {
      if (message.type() !== 'error' && message.type() !== 'warning') return;
      if (this.expectedLoadFailure(message.text(), message.location().url)) return;
      this.problems.push(`console ${message.type()}: ${message.text()} at ${message.location().url}`);
    });
    page.on('pageerror', (error) => this.problems.push(`uncaught page error: ${error.message}`));
    page.on('requestfailed', (request) => this.problems.push(`request failed: ${request.method()} ${request.url()} ${request.failure()?.errorText ?? ''}`));
    page.on('response', (response) => this.response(response));
    page.on('request', (request) => this.started(request));
    page.on('requestfinished', (request) => this.ended(request));
    page.on('requestfailed', (request) => this.ended(request));
    page.on('framenavigated', (frame) => {
      if (frame !== page.mainFrame()) return;
      this.generation += 1;
      for (const [request, generation] of this.inflight) if (generation < this.generation && !request.isNavigationRequest()) this.inflight.delete(request);
    });
  }

  async quiet(): Promise<void> {
    await expect
      .poll(() => (this.inflight.size === 0 && Date.now() - this.lastActivity >= 500 ? [] : [...this.inflight.keys()].map((request) => `${request.method()} ${request.url()}`)), {
        message: 'requests still in flight (none for 500 ms is quiet)',
        intervals: [100],
      })
      .toEqual([]);
  }

  private started(request: Request): void {
    this.inflight.set(request, this.generation);
    this.lastActivity = Date.now();
  }

  private ended(request: Request): void {
    this.inflight.delete(request);
    this.lastActivity = Date.now();
  }

  expectStatus(url: RegExp, status: number): void {
    this.expected.push({ url, status, seen: 0, logged: 0 });
  }

  async check(name: string): Promise<void> {
    const page = this.page;
    await page.waitForLoadState('load');
    await this.quiet();
    await page.waitForFunction(() =>
      document.documentElement
        .getAnimations({ subtree: true })
        .every((animation) => animation.playState !== 'running' || animation.effect?.getComputedTiming().endTime === Infinity),
    );
    const found: string[] = [];
    const broken = await page.evaluate(() =>
      [...document.querySelectorAll('img')].filter((image) => image.complete && image.naturalWidth === 0).map((image) => image.currentSrc || image.src),
    );
    found.push(...broken.map((source) => `broken image: ${source}`));
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    if (overflow > 0) found.push(`horizontal overflow: ${overflow}px wider than the ${page.viewportSize()?.width}px viewport`);
    const axe = await new AxeBuilder({ page }).withTags(wcag).analyze();
    found.push(...axe.violations.map((violation) => `axe ${violation.id}: ${violation.help} (${violation.nodes.map((node) => node.target.join(' ')).join(', ')})`));
    found.push(...checkLinks(await links(page), page.url()));
    await this.info.attach(`${name}.png`, {
      body: await page.screenshot({ fullPage: true, animations: 'disabled', caret: 'hide', mask: [page.locator(secrets)] }),
      contentType: 'image/png',
    });
    const problems = [...this.problems.splice(0), ...found];
    expect(problems, `${name} at ${page.url()}`).toEqual([]);
  }

  async finish(): Promise<void> {
    if (!this.page.isClosed()) await this.quiet();
    expect(this.problems.splice(0), 'after the last audited state').toEqual([]);
    for (const expected of this.expected) expect(expected.seen, `HTTP ${expected.status} for ${expected.url}`).toBe(1);
  }

  private expectedLoadFailure(text: string, url: string): boolean {
    const status = /^Failed to load resource: the server responded with a status of (\d{3}) /.exec(text)?.[1];
    const expected = this.expected.find((candidate) => String(candidate.status) === status && candidate.url.test(url) && candidate.logged < candidate.seen);
    if (!expected) return false;
    expected.logged += 1;
    return true;
  }

  private response(response: Response): void {
    if (response.status() < 400) return;
    const expected = this.expected.find((candidate) => candidate.status === response.status() && candidate.url.test(response.url()));
    if (expected) {
      expected.seen += 1;
      return;
    }
    this.problems.push(`HTTP ${response.status()}: ${response.request().method()} ${response.url()}`);
  }
}

async function links(page: Page): Promise<Link[]> {
  return page.evaluate(() => {
    const anchors = [...document.querySelectorAll<HTMLAnchorElement>('a[href]')].map((anchor) => ({ kind: 'link', url: anchor.href }));
    const forms = [...document.querySelectorAll<HTMLFormElement>('form')]
      .filter((form) => (form.getAttribute('method') ?? 'get').toLowerCase() === 'get')
      .map((form) => ({ kind: 'form', url: new URL(form.getAttribute('action') ?? '', form.baseURI).href }));
    return [...anchors, ...forms];
  });
}

export async function guard(context: BrowserContext): Promise<void> {
  await context.route(
    (url) => url.protocol.startsWith('http') && !stackHosts.has(url.hostname),
    (route) => route.abort('blockedbyclient'),
  );
}

export async function auditedContext(browser: Browser, options: BrowserContextOptions): Promise<BrowserContext> {
  const context = await browser.newContext(options);
  await guard(context);
  return context;
}

export const test = base.extend<{ audit: Audit }>({
  context: async ({ context }, use) => {
    await guard(context);
    await use(context);
  },
  audit: async ({ page }, use, info) => {
    const audit = new Audit(page, info);
    await use(audit);
    if (info.status === info.expectedStatus) {
      await audit.finish();
    } else if (!page.isClosed()) {
      await info.attach('failure.png', {
        body: await page.screenshot({ fullPage: true, animations: 'disabled', caret: 'hide', mask: [page.locator(secrets)] }),
        contentType: 'image/png',
      });
    }
  },
});

export { expect };
