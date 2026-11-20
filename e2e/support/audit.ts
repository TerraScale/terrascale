import AxeBuilder from '@axe-core/playwright';
import { expect, type Browser, type BrowserContext, type BrowserContextOptions, type Frame, type Page, type Request, type Response, type TestInfo } from '@playwright/test';
import { clientProxy, test as base } from './client-address';
import { checkLinks, type Link } from './links';
import { production, stackHosts } from './stack';
import { saveCookies } from './state';

const wcag = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
const reportedConsole: ReadonlySet<string> = new Set(['error', 'warning', 'assert']);
const regionPingProbe = /^https:\/\/[a-z][a-z0-9-]*\.api\.terrascale\.tech\/healthz\?p=\d+-\d+$/;
const secrets = '[data-mfa-secret], img[src^="data:image/png"], input[type=password], .ts-apikeys-secret-value, .ts-apikeys-secret-mask, #access-token-secret, #alert-secret';

type Expected = { url: RegExp; status: number; seen: number; logged: number };
type PageIdentity = Promise<string | undefined>;
type PendingRequest = { owner: PageIdentity; replacements: number };
type Replacement = { request: Request; previous: PageIdentity; committed?: PageIdentity };
type RequestFailure = {
  url: string; description: string; aborted: boolean; owner?: PageIdentity;
  observed: PageIdentity; replacement?: Replacement;
};

const activePages = new WeakMap<Page, ActivePage>();

function readPageIdentity(frame: Frame): PageIdentity {
  return frame.evaluate(() => {
    const key = Symbol.for('terrascale.journey.audit.page');
    const existing: unknown = Reflect.get(window.document, key);
    if (typeof existing === 'string') return existing;
    const identity = crypto.getRandomValues(new Uint32Array(4)).join('-');
    Object.defineProperty(window.document, key, { value: identity });
    return identity;
  }).catch(() => undefined);
}

class ActivePage {
  current: PageIdentity;
  replacements = 0;
  replacement?: Replacement;

  constructor(page: Page) {
    this.current = readPageIdentity(page.mainFrame());
    page.on('request', (request) => {
      if (!isMainFrameNavigation(page, request)) return;
      const replacement = this.replacement;
      if (replacement && request.redirectedFrom() === replacement.request) {
        replacement.request = request;
      } else {
        this.replacement = { request, previous: this.current };
      }
    });
    page.on('requestfailed', (request) => {
      if (this.replacement?.request === request) this.replacement = undefined;
    });
    page.on('framenavigated', (frame) => {
      if (frame !== page.mainFrame()) return;
      this.current = readPageIdentity(frame);
      const replacement = this.replacement;
      if (replacement && replacement.request.url().split('#', 1)[0] === frame.url().split('#', 1)[0]) {
        replacement.committed = this.current;
        this.replacement = undefined;
        this.replacements += 1;
      }
    });
  }
}

function isMainFrameNavigation(page: Page, request: Request): boolean {
  if (!request.isNavigationRequest()) return false;
  try {
    return request.frame() === page.mainFrame();
  } catch {
    return false;
  }
}

async function isPreviousPage(pending: PendingRequest, current: string | undefined, replacements: number): Promise<boolean> {
  if (current === undefined) return false;
  const identity = await pending.owner;
  return identity === undefined ? pending.replacements < replacements : identity !== current;
}

async function canceledByReplacement({ owner, observed, replacement }: RequestFailure): Promise<boolean> {
  const identity = await owner;
  if (identity === undefined) return false;
  const seen = await observed;
  if (seen !== undefined && seen !== identity) return true;
  if (!replacement?.committed) return false;
  const previous = await replacement.previous;
  const committed = await replacement.committed;
  return previous === identity && committed !== undefined && committed !== identity;
}

export class Audit {
  private problems: string[] = [];
  private readonly expected: Expected[] = [];
  private readonly inflight = new Map<Request, PendingRequest>();
  private readonly streams = new Set<Request>();
  private readonly downloads = new Set<string>();
  private readonly failed: RequestFailure[] = [];
  private readonly probeConsole: { url: string; problem: string }[] = [];
  private readonly probeFailures = new Map<string, number>();
  private readonly activePage: ActivePage;

  constructor(
    private readonly page: Page,
    private readonly info: TestInfo,
  ) {
    this.activePage = activePages.get(page) ?? new ActivePage(page);
    activePages.set(page, this.activePage);
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
      const error = request.failure()?.errorText ?? '';
      const aborted = error === 'net::ERR_ABORTED';
      if (aborted && this.streams.has(request)) return;
      this.failed.push({
        url: request.url(), description: `${request.method()} ${request.url()} ${error}`, aborted,
        owner: request.isNavigationRequest() ? undefined : this.inflight.get(request)?.owner,
        observed: this.activePage.current, replacement: this.activePage.replacement,
      });
    });
    page.on('download', (download) => this.downloads.add(download.url()));
    page.on('response', (response) => this.response(response));
    page.on('request', (request) => this.started(request));
    page.on('requestfinished', (request) => this.ended(request));
    page.on('requestfailed', (request) => this.ended(request));
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
      .poll(async () => {
        const observation = this.activePage.current;
        const replacements = this.activePage.replacements;
        const current = await observation;
        const pending = await Promise.all([...this.inflight].map(async ([request, started]) => {
          if (!request.isNavigationRequest() && await isPreviousPage(started, current, replacements)) return undefined;
          if (this.inflight.has(request)) return `${request.method()} ${request.url()}`;
        }));
        if (observation !== this.activePage.current) return ['page changed while checking requests'];
        return pending.filter((request) => request !== undefined);
      }, {
        message: 'requests still in flight',
        intervals: [100],
      })
      .toEqual([]);
  }

  private started(request: Request): void {
    if (this.isRegionPingProbe(request)) return;
    this.inflight.set(request, { owner: this.activePage.current, replacements: this.activePage.replacements });
  }

  private ended(request: Request): void {
    if (this.isRegionPingProbe(request)) return;
    this.streams.delete(request);
    this.inflight.delete(request);
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
    if (/\}\s*else if\(Model\.|@\(await |Model\.Tab/.test(await page.locator('main').innerText())) found.push('Razor source leaked into rendered main text');
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
    const problems = [...await this.failures(), ...found];
    expect(problems, `${name} at ${page.url()}`).toEqual([]);
  }

  async finish(): Promise<void> {
    if (!this.page.isClosed()) await this.quiet();
    expect(await this.failures(), 'after the last audited state').toEqual([]);
    for (const expected of this.expected) {
      expect(expected.seen, `HTTP ${expected.status} for ${expected.url}`).toBe(1);
      expect(expected.logged, `console lines for the expected HTTP ${expected.status} of ${expected.url}`).toBeLessThanOrEqual(expected.seen);
    }
  }

  private async failures(): Promise<string[]> {
    const requestProblems: string[] = [];
    let verdict: string[] = [];
    await expect.poll(async () => {
      const observation = this.activePage.current;
      const failures = this.failed.splice(0);
      const replaced = await Promise.all(failures.map((failure) => failure.aborted && canceledByReplacement(failure)));
      const stable = observation === this.activePage.current;
      requestProblems.push(...failures.filter((failure, index) =>
        !failure.aborted || !(this.downloads.has(failure.url) || (stable && replaced[index])))
        .map((failure) => `request failed: ${failure.description}`));
      if (!stable || this.failed.length > 0) return false;
      verdict = [...this.problems.splice(0), ...this.probeProblems(), ...requestProblems];
      return true;
    }, { message: 'browser audit events still pending', intervals: [100] }).toBe(true);
    return verdict;
  }

  private probeProblems(): string[] {
    return this.probeConsole.splice(0).filter((message) => {
      const remaining = (this.probeFailures.get(message.url) ?? 0) - 1;
      if (remaining < 0) return true;
      if (remaining === 0) this.probeFailures.delete(message.url);
      else this.probeFailures.set(message.url, remaining);
      return false;
    }).map((message) => message.problem);
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
    if (response.headers()['content-type']?.startsWith('text/event-stream')) {
      this.inflight.delete(response.request());
      this.streams.add(response.request());
    }
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
