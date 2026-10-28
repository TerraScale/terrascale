import assert from 'node:assert/strict';
import test from 'node:test';
import { en } from '../src/lib/i18n/messages/en.ts';
import { es } from '../src/lib/i18n/messages/es.ts';
import { ptBr } from '../src/lib/i18n/messages/pt-br.ts';

const locales = [['en', en], ['es', es], ['pt-BR', ptBr]];
// Public Alpha makes no availability or latency promise, so the home page must not imply one.
const unverifiedClaims = /\b\d{2}(?:\.\d+)?\s*%|sub[- ]?\d+\s*ms|SOC 2|\d+\+ regions/i;
const noSla = { en: /no uptime percentage or SLA/i, es: /ni SLA/i, 'pt-BR': /nem SLA/i };

for (const [locale, messages] of locales) {
  test(`${locale}: hero has one primary call to action with its risk reversal`, () => {
    assert.ok(messages.hero.cta.trim());
    assert.match(messages.hero.ctaNote, noSla[locale]);
  });

  test(`${locale}: how it works has three steps`, () => {
    assert.equal(messages.howItWorks.steps.length, 3);
    for (const step of messages.howItWorks.steps) assert.ok(step.title && step.text);
  });

  test(`${locale}: tagline reveal has at least two lines`, () => {
    assert.ok(messages.tagline.lines.length >= 2);
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
    const copy = JSON.stringify([messages.hero, messages.howItWorks, messages.tagline, messages.homeFaq, messages.features]);
    assert.doesNotMatch(copy, unverifiedClaims);
  });
}

test('every locale has the same home page shape', () => {
  const shape = (messages) => [messages.howItWorks.steps.length, messages.tagline.lines.length,
    messages.homeFaq.items.length, messages.features.items.length, messages.cta.perks.length];
  for (const [, messages] of locales) assert.deepEqual(shape(messages), shape(en));
});
