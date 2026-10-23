import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
const pricing = read('docs/pricing.md');
const limits = read('docs/limits.md');

test('subscription table preserves annual-only Starter and nullable annual prices', () => {
  assert.match(pricing, /\| Free \| Starter \| Pro \| Team \| Enterprise \|/);
  assert.match(pricing, /\| Subscription \| \$0; no card \| \$30\/year only \(\$2\.50\/month equivalent\) \| \$20\/month \| \$50\/month \| \$999\/month support \+ usage; contact sales \|/);
  assert.doesNotMatch(pricing, /undefined|null|\$NaN|\| Annual price \|/);
  assert.match(pricing, /No trials or annual Pro\/Team discounts/);
});

test('approved usage rates include client egress and storage time without claiming collection', () => {
  assert.match(pricing, /\$5 per million/);
  assert.match(pricing, /\$0\.50 per GB-region-month/);
  assert.match(pricing, /\$0\.10 per decimal GB/);
  assert.match(pricing, /averaged over time during the month/);
  assert.match(pricing, /excluding replication traffic/);
  assert.match(pricing, /Usage charges are not yet collected/);
  assert.match(pricing, /Approved subscription prices, allowances and usage rates/);
  assert.match(pricing, /Approved pricing does not establish current overage enforcement or payment collection/);
  assert.match(pricing, /\| Storage \(GB-region-month\) \| 100 MB \| 1 GB \| 10 GB \| 25 GB \| 100 GB \|/);
  assert.match(pricing, /\| Operation units \/ month \| 50,000 \| 250,000 \| 2,000,000 \| 5,000,000 \| 10,000,000 \|/);
  assert.match(pricing, /\| Client egress \/ month \| 1 GB \| 5 GB \| 50 GB \| 100 GB \| 250 GB \|/);
  assert.doesNotMatch(pricing, /proposed allowances|proposed rates|pending review/i);
  assert.doesNotMatch(pricing, /Hobby|verified card|no paid overages|include.*30-day trial/);
});

test('existing engine safety tiers do not invent commercial PITR entitlements', () => {
  assert.match(limits, /Hobby \| Pro \| Enterprise/);
  assert.match(limits, /No mapping between commercial plans and engine tiers is published/);
  assert.match(limits, /hobby 1 \/ pro 7 \/ enterprise 30/);
  assert.doesNotMatch(pricing, /PITR retention/);
  assert.doesNotMatch(limits, /Free 1 \/ Starter .*PITR/);
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

test('pricing overview, usage accounting and terms publish approved pricing with collection caveats', () => {
  for (const path of ['docs/pricing/index.md', 'docs/concepts/pricing-philosophy.md', 'docs/legal/terms.md']) {
    const copy = read(path);
    assert.match(copy, /approved/i, path);
    assert.match(copy, /usage charges are not yet collected/i, path);
    assert.doesNotMatch(copy, /proposed allowances|proposed rates|pending review|propose included/i, path);
  }
});
