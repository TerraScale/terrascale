import assert from 'node:assert/strict';
import test from 'node:test';
import { en } from '../src/lib/i18n/messages/en.ts';
import { es } from '../src/lib/i18n/messages/es.ts';
import { ptBr } from '../src/lib/i18n/messages/pt-br.ts';

const locales = [['en', en], ['es', es], ['pt-BR', ptBr]];
// Public Alpha makes no availability or latency promise, so the home page must not imply one.
const unverifiedClaims = /\b\d{2}(?:\.\d+)?\s*%|sub[- ]?\d+\s*ms|SOC 2|\d+\+ regions/i;
const noSla = { en: /no uptime percentage\b.*\bSLA/i, es: /ni SLA/i, 'pt-BR': /nem SLA/i };

for (const [locale, messages] of locales) {
  test(`${locale}: hero has one primary call to action and the FAQ states there is no SLA`, () => {
    assert.ok(messages.hero.cta.trim());
    assert.ok(messages.homeFaq.items.some((item) => noSla[locale].test(item.a)));
  });

  // The approved Contact design explicitly includes its section label and introduction, and the
  // approved footer redesign carries the product tagline. Keep the Simple UI restriction
  // everywhere else, with only these three exact exceptions.
  test(`${locale}: secondary copy is limited to the approved Contact and footer exceptions`, () => {
    const keys = [];
    const walk = (value, path) => {
      if (Array.isArray(value)) value.forEach((item, i) => walk(item, `${path}[${i}]`));
      else if (value && typeof value === 'object') {
        for (const [key, child] of Object.entries(value)) { keys.push(`${path}.${key}`); walk(child, `${path}.${key}`); }
      }
    };
    walk(messages, locale);
    assert.deepEqual(keys.filter((key) => /\.(?:\w*eyebrow|subtitle|tagline|ctaNote|perks|highlights)$/i.test(key)),
      [`${locale}.footer.tagline`, `${locale}.contact.eyebrow`, `${locale}.contact.subtitle`]);
    assert.ok(messages.contact.eyebrow.trim() && messages.contact.subtitle.trim());
  });

  test(`${locale}: how it works has three steps`, () => {
    assert.equal(messages.howItWorks.steps.length, 3);
    for (const step of messages.howItWorks.steps) assert.ok(step.title && step.text);
  });

  test(`${locale}: home FAQ answers six to twelve questions`, () => {
    const { items } = messages.homeFaq;
    assert.ok(items.length >= 6 && items.length <= 12);
    assert.equal(new Set(items.map((item) => item.q)).size, items.length);
  });

  test(`${locale}: every feature card leads with a benefit headline`, () => {
    for (const item of messages.features.items) assert.ok(item.headline.trim());
  });

  test(`${locale}: home copy makes no unverified availability claims`, () => {
    const copy = JSON.stringify([messages.hero, messages.howItWorks, messages.homeFaq, messages.features]);
    assert.doesNotMatch(copy, unverifiedClaims);
  });
}

test('every locale has the same home page shape', () => {
  const shape = (messages) => [messages.howItWorks.steps.length, messages.homeFaq.items.length,
    messages.features.items.length, messages.code.agent.prompts.length];
  for (const [, messages] of locales) assert.deepEqual(shape(messages), shape(en));
});
