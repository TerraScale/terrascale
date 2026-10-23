import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { formatPlanBilling, formatPlanLimits } from '../src/lib/pricing.ts';
import { en } from '../src/lib/i18n/messages/en.ts';
import { es } from '../src/lib/i18n/messages/es.ts';
import { ptBr } from '../src/lib/i18n/messages/pt-br.ts';

const catalog = JSON.parse(readFileSync(new URL('../static/pricing.json', import.meta.url), 'utf8'));
const locales = [['en', en], ['es', es], ['pt-BR', ptBr]];

for (const [locale, messages] of locales) {
  test(`${locale}: all five plans have coherent prices, cadence, and allowances`, () => {
    assert.deepEqual(Object.keys(messages.pricing.plans), catalog.plans.map((p) => p.id));
    for (const plan of catalog.plans) {
      const billing = formatPlanBilling({
        price: plan.priceMonthlyUsd, annualPrice: plan.priceAnnualUsd, billingInterval: plan.billingInterval,
      }, messages.pricing, locale);
      assert.ok(!/null|undefined|NaN|\{/.test(Object.values(billing).join(' ')));
      if (plan.id === 'starter') {
        assert.match(billing.price, /30/);
        assert.equal(billing.suffix, messages.pricing.suffixAnnual);
        assert.match(billing.cadence, /2[.,]50/);
      } else if (plan.id === 'free') {
        assert.equal(billing.suffix, '');
        assert.equal(billing.cadence, messages.pricing.cadence.free);
      } else {
        assert.ok(billing.price.includes(String(plan.priceMonthlyUsd)));
        assert.equal(billing.suffix, plan.id === 'enterprise' ? messages.pricing.suffixUsage : messages.pricing.suffixMonthly);
      }
      const limits = formatPlanLimits(plan.limits, messages.pricing.limits, locale);
      assert.equal(limits.length, plan.id === 'enterprise' ? 3 : 6);
      assert.ok(!limits.some((line) => /null|undefined|NaN|\{/.test(line)));
    }
    assert.match(formatPlanLimits(catalog.plans[0].limits, messages.pricing.limits, locale).join(' '), /100 MB/);
    assert.equal(messages.pricing.detailed.rows.length, 3);
  });
}
