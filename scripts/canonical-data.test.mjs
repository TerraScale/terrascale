import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import timers from 'node:timers/promises';
import { fetchCanonical, main, validateCanonical } from './canonical-data.mjs';

test('region validation rejects stale providers, names, and provider identifiers independently of byte parity', async (t) => {
  const text = readFileSync(new URL('../data/canonical/regions.json', import.meta.url), 'utf8');
  assert.doesNotThrow(() => validateCanonical('regions.json', text));
  for (const [field, value] of [['provider', 'fly'], ['name', 'San Jose'], ['providerRegion', 'sjc']]) {
    const catalog = JSON.parse(text);
    catalog.regions.find((region) => region.id === 'sjc')[field] = value;
    const stale = JSON.stringify(catalog);
    assert.throws(() => validateCanonical('regions.json', stale), /OCI launch regions/);
    const fetch = t.mock.method(globalThis, 'fetch', async () => new Response(stale));
    await assert.rejects(fetchCanonical('regions.json'), /OCI launch regions/);
    assert.equal(fetch.mock.callCount(), 1);
    fetch.mock.restore();
  }
});

test('catalog transport errors get one retry after a mocked five-second wait', async (t) => {
  for (const error of [
    new TypeError('fetch failed', { cause: { code: 'ECONNRESET' } }),
    new DOMException('timed out', 'TimeoutError'),
    new TypeError('fetch failed', { cause: { code: 'ENOTFOUND' } }),
  ]) {
    await t.test(error.cause?.code ?? error.name, async (t) => {
      const sleep = t.mock.method(timers, 'setTimeout', async (ms) => assert.equal(ms, 5_000));
      t.mock.method(console, 'warn', () => {});
      let calls = 0;
      const fetch = t.mock.method(globalThis, 'fetch', async (url, options) => {
        assert.equal(url, 'https://terrascale.tech/pricing.json');
        assert.equal(options.headers.accept, 'application/json');
        assert.ok(options.signal instanceof AbortSignal);
        if (++calls === 1) throw error;
        return new Response('{"price":1}\n');
      });
      assert.equal(await fetchCanonical('pricing.json'), '{"price":1}\n');
      assert.equal(fetch.mock.callCount(), 2);
      assert.equal(sleep.mock.callCount(), 1);
    });
  }
});

test('a second transport failure is fatal with no further wait', async (t) => {
  const error = new TypeError('fetch failed', { cause: { code: 'ECONNRESET' } });
  const sleep = t.mock.method(timers, 'setTimeout', async () => {});
  t.mock.method(console, 'warn', () => {});
  const fetch = t.mock.method(globalThis, 'fetch', async () => { throw error; });
  await assert.rejects(fetchCanonical('regions.json'), (actual) => actual === error);
  assert.equal(fetch.mock.callCount(), 2);
  assert.equal(sleep.mock.callCount(), 1);
});

test('HTTP errors and invalid JSON fail without a retry', async (t) => {
  for (const [response, message] of [
    [new Response('unavailable', { status: 503 }), /HTTP 503/],
    [new Response('invalid JSON'), /Unexpected token/],
  ]) {
    await t.test(message.source, async (t) => {
      const sleep = t.mock.method(timers, 'setTimeout', async () => {});
      const fetch = t.mock.method(globalThis, 'fetch', async () => response);
      await assert.rejects(fetchCanonical('pricing.json'), message);
      assert.equal(fetch.mock.callCount(), 1);
      assert.equal(sleep.mock.callCount(), 0);
    });
  }
});

test('an unrelated TypeError is not treated as a transport failure', async (t) => {
  const error = new TypeError('invalid fetch options');
  const sleep = t.mock.method(timers, 'setTimeout', async () => {});
  const fetch = t.mock.method(globalThis, 'fetch', async () => { throw error; });
  await assert.rejects(fetchCanonical('pricing.json'), (actual) => actual === error);
  assert.equal(fetch.mock.callCount(), 1);
  assert.equal(sleep.mock.callCount(), 0);
});

test('byte parity mismatches still fail without retrying either catalog', async (t) => {
  const sleep = t.mock.method(timers, 'setTimeout', async () => {});
  const fetch = t.mock.method(globalThis, 'fetch', async (url) => {
    const file = new URL(url).pathname.slice(1);
    return new Response(readFileSync(new URL(`../data/canonical/${file}`, import.meta.url), 'utf8') + '\n');
  });
  t.mock.method(console, 'log', () => {});
  const errors = t.mock.method(console, 'error', () => {});
  const exit = t.mock.method(process, 'exit', () => {});
  await main();
  assert.equal(fetch.mock.callCount(), 2);
  assert.equal(sleep.mock.callCount(), 0);
  assert.equal(errors.mock.callCount(), 2);
  for (const call of errors.mock.calls) assert.match(call.arguments[0], /drifted/);
  assert.equal(exit.mock.callCount(), 1);
  assert.deepEqual(exit.mock.calls[0].arguments, [1]);
});
