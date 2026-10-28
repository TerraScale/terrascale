import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
const pricing = read('docs/pricing.md');
const limits = read('docs/limits.md');

test('subscription table offers every paid plan monthly and yearly, two months free', () => {
  assert.match(pricing, /\| Free \| Starter \| Pro \| Team \| Enterprise \|/);
  assert.match(pricing, /\| Subscription \| \$0; no card \| \$3\/month or \$30\/year \| \$20\/month or \$200\/year \| \$50\/month or \$500\/year \| \$999\/month support \+ usage; contact sales \|/);
  assert.doesNotMatch(pricing, /undefined|null|\$NaN|\| Annual price \||year only|annual Pro\/Team discounts/);
  assert.match(pricing, /A year costs 10 monthly payments, so yearly billing saves\n2 months\. No trials are offered\./);
});

test('approved usage rates bill operation-unit overages and defer storage and egress', () => {
  assert.match(pricing, /\$5 per million/);
  assert.match(pricing, /\$0\.50 per GB-region-month/);
  assert.match(pricing, /\$0\.10 per decimal GB/);
  assert.match(pricing, /averaged over time during the month/);
  assert.match(pricing, /excluding replication traffic/);
  assert.match(pricing, /Approved subscription prices, allowances and usage rates/);
  assert.match(pricing, /operation units over the allowance are added to the next subscription invoice at the overage rate/);
  assert.match(pricing, /Storage and client egress over the allowance are not billed yet/);
  assert.match(pricing, /new writes stop at its operation allowance and there are no overage charges/);
  assert.match(pricing, /\| Availability \| Sign up \| Checkout \| Checkout \| Checkout \| Contact sales \|/);
  assert.doesNotMatch(pricing, /not yet collected|Confirm enrollment|confirm enrollment|self-service purchase|does not establish/);
  assert.match(pricing, /\| Storage \(GB-region-month\) \| 100 MB \| 1 GB \| 10 GB \| 25 GB \| 100 GB \|/);
  assert.match(pricing, /\| Operation units \/ month \| 50,000 \| 250,000 \| 2,000,000 \| 5,000,000 \| 10,000,000 \|/);
  assert.match(pricing, /\| Client egress \/ month \| 1 GB \| 5 GB \| 50 GB \| 100 GB \| 250 GB \|/);
  assert.doesNotMatch(pricing, /proposed allowances|proposed rates|pending review/i);
  assert.doesNotMatch(pricing, /Hobby|verified card|no paid overages|include.*30-day trial/);
});

test('limits publish per-plan entitlements from the catalog and the engine-tier mapping', () => {
  const catalog = JSON.parse(read('data/canonical/pricing.json'));
  assert.ok(catalog.plans.every((p) => p.entitlements && Object.keys(p.entitlements).length === 9));
  assert.match(limits, /## Plan entitlements/);
  assert.match(limits, /Free maps to the hobby engine tier; other plans map to the tier of the same name/);
  assert.match(limits, /HTTP 429 with Retry-After/);
  assert.match(limits, /\| Entitlement \(per organization\) \| Free \| Starter \| Pro \| Team \| Enterprise \|/);
  assert.match(limits, /\| Requests\/s \(burst\) \| 50 \(100\) \| 200 \(400\) \| 500 \(1,000\) \| 500 \(1,000\) \| No plan ceiling \|/);
  assert.match(limits, /\| Storage limit \| 100 MB \| 5 GB \| 100 GB \| 100 GB \| No plan ceiling \|/);
  assert.match(limits, /\| Point-in-time recovery window \| 1 day \| 3 days \| 7 days \| 14 days \| 30 days \|/);
  assert.match(limits, /\| Support response target \| Community \| 2 business days \| 1 business day \| 8 hours \| Custom \|/);
  assert.match(limits, /response targets, not an SLA/);
  assert.doesNotMatch(limits, /undefined|null|NaN|No mapping|mapping is published|Hobby \| Pro \| Enterprise|not yet collected/);
  const support = read('docs/legal/support.md');
  for (const p of catalog.plans) assert.match(support, new RegExp(`\\| ${p.name} \\| ${p.entitlements.support}`));
  assert.match(support, /support@terrascale\.tech/);
  assert.match(support, /not a service-level agreement/);
  assert.doesNotMatch(support, /Draft|noindex|not yet in effect|response deadline/);
  assert.doesNotMatch(read('docs/pricing/index.md'), /no mapping is published/);
  assert.match(read('docs/guides/backups-and-restore.md'), /\/limits#plan-entitlements/);
  assert.doesNotMatch(read('data/policies.json'), /Draft/);
});

test('operation units count writes once and exclude regional replication', () => {
  for (const copy of [pricing, read('docs/pricing/index.md'), read('docs/concepts/pricing-philosophy.md')]) {
    assert.match(copy, /Writes count once regardless of the number of selected regions/);
    assert.match(copy, /Replication between regions is not billed/);
    assert.doesNotMatch(copy, /writes (?:count|consume operation units) once per selected region/i);
  }
});

test('shared organization allowances never multiply writes by selected regions', () => {
  const policies = JSON.parse(read('data/policies.json'));
  assert.match(policies.sharedAllowances, /Each 4 KiB written counts once, no matter how many regions hold the database/);
  assert.match(policies.sharedAllowances, /replication between regions is free/);
  assert.ok(pricing.includes(policies.sharedAllowances));
});

test('pricing overview and usage accounting publish the overage rule', () => {
  for (const path of ['docs/pricing/index.md', 'docs/concepts/pricing-philosophy.md']) {
    const copy = read(path);
    assert.match(copy, /approved/i, path);
    assert.match(copy, /added to the next subscription invoice at the overage rate/, path);
    assert.match(copy, /storage and client egress over the allowance are not billed yet/, path);
    assert.doesNotMatch(copy, /not yet collected/i, path);
    assert.doesNotMatch(copy, /proposed allowances|proposed rates|pending review|propose included/i, path);
  }
});
