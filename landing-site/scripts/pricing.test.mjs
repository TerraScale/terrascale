import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { annualSavingMonths, formatEntitlementRows, formatPlanBilling, formatPlanCadences, formatPlanLimits } from '../src/lib/pricing.ts';
import { en } from '../src/lib/i18n/messages/en.ts';
import { es } from '../src/lib/i18n/messages/es.ts';
import { ptBr } from '../src/lib/i18n/messages/pt-br.ts';

const catalog = JSON.parse(readFileSync(new URL('../static/pricing.json', import.meta.url), 'utf8'));
const locales = [['en', en], ['es', es], ['pt-BR', ptBr]];
const billed = (plan) => ({ price: plan.priceMonthlyUsd, annualPrice: plan.priceAnnualUsd, available: plan.available, billingIntervals: plan.billingIntervals });
const usageCaveats = {
  en: /storage over it is billed per GB-month; client egress overage is not billed yet/i,
  es: /almacenamiento por encima de ella se factura por GB-mes; el excedente de salida a clientes aún no se factura/i,
  'pt-BR': /armazenamento acima dela é cobrado por GB-mês; o excedente de saída para clientes ainda não é cobrado/i,
};
const overageRules = { en: /next (?:subscription )?invoice/i, es: /próxima factura/i, 'pt-BR': /próxima fatura/i };
const staleClaims = /not yet collected|aún no se cobran|ainda não são coletadas|self-service purchase|compra directa|contratação direta|confirm (?:paid plan )?enrollment/i;
const operationRules = { en: /each started 4 KiB/i, es: /cada 4 KiB.*iniciados/i, 'pt-BR': /cada 4 KiB.*iniciados/i };
// Each database lives in one home region, so no copy may describe or price replication.
const replication = /replic/i;

for (const [locale, messages] of locales) {
  test(`${locale}: operation units count each started 4 KiB and never mention replication`, () => {
    for (const copy of [messages.pricing.triad[1].text, messages.pricing.detailed.rows[0].details,
      messages.pricing.faq.items[0].a]) {
      assert.match(copy, operationRules[locale]);
    }
    assert.doesNotMatch(JSON.stringify(messages), replication);
    assert.doesNotMatch(JSON.stringify(catalog), replication);
    assert.ok(catalog.metering.operationUnit.rules.includes('Each database lives in one home region; writes count there.'));
  });
  test(`${locale}: pricing states the overage rule, storage billing and the egress caveat`, () => {
    const pricingCopy = JSON.stringify(messages.pricing);
    assert.doesNotMatch(pricingCopy, /propos|pending review|pendientes de revisión|pendentes de revisão|propõem|proponen/i);
    assert.doesNotMatch(JSON.stringify([messages.pricing, messages.cta, messages.about]), staleClaims);
    for (const copy of [messages.pricing.detailed.text, messages.pricing.faq.items[1].a]) {
      assert.match(copy, overageRules[locale]);
    }
    // Free, Starter, Pro and Team each have their own checkout action; Enterprise goes to sales.
    for (const plan of Object.values(messages.pricing.plans)) assert.ok(plan.cta.trim());
    assert.equal(new Set(Object.values(messages.pricing.plans).map((plan) => plan.cta)).size, 5);
    for (const copy of [messages.pricing.triad[2].text, messages.pricing.detailed.text,
      messages.pricing.faq.items[1].a, messages.about.pillars[2].text]) {
      assert.match(copy, usageCaveats[locale]);
    }
  });
  test(`${locale}: all five plans have coherent prices, cadence, and allowances`, () => {
    assert.deepEqual(Object.keys(messages.pricing.plans), catalog.plans.map((p) => p.id));
    assert.equal(annualSavingMonths(catalog.plans.map(billed)), 2);
    for (const plan of catalog.plans) {
      const monthly = formatPlanBilling(billed(plan), 'month', messages.pricing, locale);
      const yearly = formatPlanBilling(billed(plan), 'year', messages.pricing, locale);
      const cadences = formatPlanCadences(billed(plan), messages.pricing, locale);
      for (const text of [...Object.values(monthly), ...Object.values(yearly), cadences]) assert.ok(!/null|undefined|NaN|\{/.test(text));
      if (plan.id === 'free') {
        for (const billing of [monthly, yearly]) {
          assert.equal(billing.suffix, '');
          assert.equal(billing.cadence, messages.pricing.cadence.free);
        }
      } else if (plan.id === 'enterprise') {
        assert.deepEqual(yearly, monthly);
        assert.equal(monthly.suffix, messages.pricing.suffixUsage);
      } else {
        // Every paid plan sells monthly and yearly; a year costs ten monthly payments.
        assert.deepEqual(plan.billingIntervals, ['month', 'year']);
        assert.equal(plan.priceAnnualUsd, plan.priceMonthlyUsd * 10);
        assert.ok(monthly.price.includes(String(plan.priceMonthlyUsd)));
        assert.equal(monthly.suffix, messages.pricing.suffixMonthly);
        assert.ok(yearly.price.includes(String(plan.priceAnnualUsd)));
        assert.equal(yearly.suffix, messages.pricing.suffixAnnual);
        assert.match(yearly.cadence, /\b2\b/);
        assert.ok(cadences.includes(String(plan.priceMonthlyUsd)) && cadences.includes(String(plan.priceAnnualUsd)));
      }
      if (plan.id === 'starter') assert.match(yearly.cadence, /2[.,]50/);
      const limits = formatPlanLimits(plan.limits, messages.pricing.limits, locale);
      assert.equal(limits.length, plan.id === 'enterprise' ? 3 : 6);
      assert.ok(!limits.some((line) => /null|undefined|NaN|\{/.test(line)));
    }
    assert.match(formatPlanLimits(catalog.plans[0].limits, messages.pricing.limits, locale).join(' '), /100 MB/);
    assert.equal(messages.pricing.detailed.rows.length, 3);
  });
  test(`${locale}: entitlement rows render every plan without null or NaN`, () => {
    const plans = catalog.plans.map((plan) => ({ slug: plan.id, entitlements: plan.entitlements }));
    const rows = formatEntitlementRows(plans, messages.pricing.compare, locale);
    assert.equal(rows.length, 9);
    for (const row of rows) {
      assert.ok(row.label);
      assert.equal(row.cells.length, catalog.plans.length);
      assert.ok(row.cells.every((cell) => typeof cell === 'string' && cell && !/null|undefined|NaN|\{/.test(cell)));
    }
    const enterprise = rows.map((row) => row.cells[catalog.plans.length - 1]);
    assert.deepEqual(enterprise.slice(0, 5), Array(5).fill(messages.pricing.compare.noCeiling));
    const free = rows.map((row) => row.cells[0]);
    assert.match(free[0], /^50$/);
    assert.match(free[4], /100 MB/);
    assert.match(rows[5].cells[3], /14/);
    assert.match(messages.pricing.compare.entitlementsNote, /429/);
    assert.match(messages.pricing.compare.entitlementsNote, /Retry-After/);
    assert.match(messages.pricing.compare.entitlementsNote, /SLA/);
    if (locale === 'en') {
      assert.deepEqual(rows[8].cells, catalog.plans.map((plan) => plan.entitlements.support));
    }
  });
}

test('catalog entitlements match the enforced engine tiers', () => {
  const byId = Object.fromEntries(catalog.plans.map((plan) => [plan.id, plan.entitlements]));
  assert.deepEqual(byId.free, { requestsPerSecond: 50, burstRequests: 100, concurrentRequests: 4, queryTimeoutSeconds: 1,
    storageLimitGb: 0.1, pitrDays: 1, backupRetentionDays: 1, regionsPerDatabase: 1, support: 'Community' });
  assert.equal(byId.enterprise.requestsPerSecond, null);
  assert.equal(byId.team.pitrDays, 14);
  assert.equal(catalog.metering.allowances.paidOverages, true);
  assert.match(catalog.metering.allowances.definition, /next subscription invoice/);
  assert.match(catalog.metering.allowances.definition, /not billed yet/);
  assert.match(catalog.metering.entitlements.definition, /Retry-After/);
});
