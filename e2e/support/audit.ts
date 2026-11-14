import AxeBuilder from '@axe-core/playwright';
import { expect, type Browser, type BrowserContext, type BrowserContextOptions, type Page, type Request, type Response, type TestInfo } from '@playwright/test';
import { clientProxy, test as base } from './client-address';
import { checkLinks, type Link } from './links';
import { production, stackHosts } from './stack';
import { saveCookies } from './state';

const wcag = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
const reportedConsole: ReadonlySet<string> = new Set(['error', 'warning', 'assert']);
const regionPingProbe = /^https:\/\/[a-z][a-z0-9-]*\.api\.terrascale\.tech\/healthz\?p=\d+-\d+$/;
const secrets = '[data-mfa-secret], img[src^="data:image/png"], input[type=password], .ts-apikeys-secret-value, .ts-apikeys-secret-mask, #access-token-secret, #alert-secret';

type Expected = { url: RegExp; status: number; seen: number; logged: number };

export class Audit {
  private problems: string[] = [];
  private readonly expected: Expected[] = [];
  private readonly inflight = new Map<Request, number>();
  private readonly downloads = new Set<string>();
  private failed: { url: string; error: string }[] = [];
  private readonly probeConsole: { url: string; problem: string }[] = [];
  private readonly probeFailures = new Map<string, number>();
  private generation = 0;
  private lastActivity = Date.now();

  constructor(
    private readonly page: Page,
    private readonly info: TestInfo,
  ) {
    page.on('console', (message) => {
      if (!reportedConsole.has(message.type())) return;
      if (this.expectedLoadFailure(message.text(), message.location().url)) return;
      if (message.type() === 'error' && message.text().startsWith('Failed to load resource: net::') && regionPingProbe.test(message.location().url)) {
        this.probeConsole.push({ url: message.location().url, problem: `console error: ${message.text()} at ${message.location().url}` });
        return;
      }
      this.problems.push(`console ${message.type()}: ${message.text()} at ${message.location().url}`);
    });
    page.on('pageerror', (error) => this.problems.push(`uncaught page error: ${error.message}`));
    page.on('requestfailed', (request) => {
      if (this.isRegionPingProbe(request)) {
        this.probeFailures.set(request.url(), (this.probeFailures.get(request.url()) ?? 0) + 1);
        return;
      }
      this.failed.push({ url: request.url(), error: `${request.method()} ${request.url()} ${request.failure()?.errorText ?? ''}` });
    });
    page.on('download', (download) => this.downloads.add(download.url()));
    page.on('response', (response) => this.response(response));
    page.on('request', (request) => this.started(request));
    page.on('requestfinished', (request) => this.ended(request));
    page.on('requestfailed', (request) => this.ended(request));
    page.on('framenavigated', (frame) => {
      if (frame !== page.mainFrame()) return;
      this.generation += 1;
      for (const [request, generation] of this.inflight) if (generation < this.generation && !request.isNavigationRequest()) this.inflight.delete(request);
    });
    if (production) {
      const reached = { type: 'url', description: page.url() };
      info.annotations.push(reached);
      page.on('framenavigated', (frame) => {
        if (frame === page.mainFrame()) reached.description = frame.url();
      });
    }
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
    if (this.isRegionPingProbe(request)) return;
    this.inflight.set(request, this.generation);
    this.lastActivity = Date.now();
  }

  private ended(request: Request): void {
    if (this.isRegionPingProbe(request)) return;
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
    if (!production) {
      await this.info.attach(`${name}.png`, {
        body: await page.screenshot({ fullPage: true, animations: 'disabled', caret: 'hide', mask: [page.locator(secrets)] }),
        contentType: 'image/png',
      });
    }
    const problems = [...this.problems.splice(0), ...this.failures(), ...found];
    expect(problems, `${name} at ${page.url()}`).toEqual([]);
  }

  async finish(): Promise<void> {
    if (!this.page.isClosed()) await this.quiet();
    expect([...this.problems.splice(0), ...this.failures()], 'after the last audited state').toEqual([]);
    for (const expected of this.expected) {
      expect(expected.seen, `HTTP ${expected.status} for ${expected.url}`).toBe(1);
      expect(expected.logged, `console lines for the expected HTTP ${expected.status} of ${expected.url}`).toBeLessThanOrEqual(expected.seen);
    }
  }

  private failures(): string[] {
    const consoleProblems = this.probeConsole.splice(0).filter((message) => {
      const remaining = (this.probeFailures.get(message.url) ?? 0) - 1;
      if (remaining < 0) return true;
      if (remaining === 0) this.probeFailures.delete(message.url);
      else this.probeFailures.set(message.url, remaining);
      return false;
    }).map((message) => message.problem);
    return [
      ...consoleProblems,
      ...this.failed.splice(0)
        .filter((failure) => !(this.downloads.has(failure.url) && failure.error.endsWith(' net::ERR_ABORTED')))
        .map((failure) => `request failed: ${failure.error}`),
    ];
  }

  private isRegionPingProbe(request: Request): boolean {
    return request.method() === 'GET' && regionPingProbe.test(request.url());
  }

  private expectedLoadFailure(text: string, url: string): boolean {
    const status = /^Failed to load resource: the server responded with a status of (\d{3}) /.exec(text)?.[1];
    const expected = this.expected.find((candidate) => String(candidate.status) === status && candidate.url.test(url));
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

async function guard(context: BrowserContext): Promise<void> {
  await context.route(
    (url) => url.protocol.startsWith('http') && !stackHosts.has(url.hostname),
    (route) => route.abort('blockedbyclient'),
  );
}

export async function auditedContext(browser: Browser, options: BrowserContextOptions): Promise<BrowserContext> {
  const context = await browser.newContext({ ...options, proxy: clientProxy() });
  await guard(context);
  return context;
}

export const test = base.extend<{ audit: Audit }>({
  context: async ({ context }, use) => {
    await guard(context);
    await use(context);
    await saveCookies(context);
  },
  audit: async ({ page }, use, info) => {
    const audit = new Audit(page, info);
    await use(audit);
    if (info.status === info.expectedStatus) {
      await audit.finish();
    } else if (!production && !page.isClosed()) {
      await info.attach('failure.png', {
        body: await page.screenshot({ fullPage: true, animations: 'disabled', caret: 'hide', mask: [page.locator(secrets)] }),
        contentType: 'image/png',
      });
    }
  },
});

export { expect };
