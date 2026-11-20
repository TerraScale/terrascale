import { randomUUID } from 'node:crypto';
import { appendFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import type { FullConfig, FullResult, Reporter, Suite, TestCase } from '@playwright/test/reporter';
import { envelopeMarker, envelopeReceipt } from './production-envelope.mjs';
import { quarantine, quarantined } from './quarantine.mjs';

export default class QuarantineReporter implements Reporter {
  private suite!: Suite;
  private errors = false;

  onBegin(_config: FullConfig, suite: Suite): void {
    this.suite = suite;
  }

  onError(): void { this.errors = true; }

  private identity(test: TestCase) {
    const titles = test.titlePath().slice(3);
    return { spec: relative(join(__dirname, '..'), test.location.file), title: titles.join(' › '), titles, project: test.parent.project()!.name };
  }

  async onEnd(result: FullResult): Promise<{ status: FullResult['status'] }> {
    const entries = quarantine();
    const cases = this.suite?.allTests() ?? [];
    const phase = process.env.E2E_PRODUCTION_PHASE;
    const state = process.env.E2E_STATE;
    const infrastructure = Boolean(phase && (result.status === 'timedout' || (state && existsSync(envelopeReceipt(state, phase))))) || cases.some(test =>
      test.results.some(attempt => attempt.errors.some(error => (error.message ?? error.value ?? '').includes(envelopeMarker))));
    if (infrastructure) console.log(`${envelopeMarker} infrastructure failure recorded`);
    const tests = cases.map(test => ({ ...this.identity(test), status: test.outcome() }));
    const failures = tests.filter(test => test.status === 'unexpected');
    const quarantinedFailures = infrastructure ? [] : failures.filter(test => quarantined(test, entries, tests));
    if (process.env.GITHUB_STEP_SUMMARY && quarantinedFailures.length) appendFileSync(process.env.GITHUB_STEP_SUMMARY,
      `\n### Quarantined failures\n${quarantinedFailures.map(test => `- ${test.spec} › ${test.title} — ${quarantined(test, entries, tests)!.url}`).join('\n')}\n`);
    if (process.env.JOURNEY_RESULTS) {
      mkdirSync(process.env.JOURNEY_RESULTS, { recursive: true });
      writeFileSync(join(process.env.JOURNEY_RESULTS, `${randomUUID()}.json`), JSON.stringify({
        run: process.env.GITHUB_RUN_ID, attempt: Number(process.env.GITHUB_RUN_ATTEMPT), tests, ...(infrastructure ? { infrastructure } : {}),
      }));
    }
    if (infrastructure && result.status === 'passed') return { status: 'failed' };
    return { status: result.status === 'failed' && !this.errors && failures.length > 0 && failures.length === quarantinedFailures.length
      && process.env.GITHUB_EVENT_NAME !== 'pull_request' ? 'passed' : result.status };
  }
}
