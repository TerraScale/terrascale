import assert from 'node:assert/strict';
import test from 'node:test';
import timers from 'node:timers/promises';
import { checkResponse } from './check-hosted.mjs';

test('new assets must match every check after one propagation retry', async (t) => {
  const sleep = t.mock.method(timers, 'setTimeout', async (ms) => assert.equal(ms, 30_000));
  t.mock.method(console, 'warn', () => {});
  let calls = 0;
  t.mock.method(globalThis, 'fetch', async () => new Response(++calls === 1 ? 'not ready' : 'checked bytes', {
    status: calls === 1 ? 404 : 200, headers: { 'content-type': 'application/javascript' },
  }));
  await checkResponse('https://docs.example/app.js', Buffer.from('checked bytes'), 200, 'javascript');
  assert.equal(calls, 2);
  assert.equal(sleep.mock.callCount(), 1);
});

test('two identical content failures remain fatal and are not retried again', async (t) => {
  const sleep = t.mock.method(timers, 'setTimeout', async (ms) => assert.equal(ms, 30_000));
  t.mock.method(console, 'warn', () => {});
  let calls = 0;
  t.mock.method(globalThis, 'fetch', async () => {
    calls++;
    return new Response('wrong bytes', { headers: { 'content-type': 'text/html' } });
  });
  await assert.rejects(checkResponse('https://docs.example/', Buffer.from('checked bytes'), 200, 'text/html'), /hosted content differs/);
  assert.equal(calls, 2);
  assert.equal(sleep.mock.callCount(), 1);
});
