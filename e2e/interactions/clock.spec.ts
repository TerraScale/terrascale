import { test, expect } from '@playwright/test';
import { production } from '../support/stack';
import { cluster, id, lone, type Transaction } from '../clock/cluster';

test.skip(production, 'clock delivery runs only against throwaway nodes');

test('future remote stamps apply once without advancing the local clock beyond its bound', async ({ request }, info) => {
  const { node, first, second } = lone('d', 71, 18350, 'far');
  const { metric, standIn, opens, clockPeer, prepare, database, finish, observations } = await cluster(request, [node, first, second], 'delivery', true);
  try {
    await prepare();
    await standIn(first, clockPeer({ offset: 0 }));
    await standIn(second, clockPeer({ offset: 0 }));
    await opens(node, 'receiver with agreeing peers');
    const { sql, deliver } = await database(node);
    const supersededCount = () => metric(node, 'terrabase_home_apply_superseded_total', `region="${first.region}"`);
    const baseline = await supersededCount();
    const received = (transaction: Transaction) => deliver(first, transaction);
    const current: Transaction = {
      id: id(), region: first.region,
      hlc: { physicalMilliseconds: Date.now() - 10_000, logical: 0, nodeId: first.name },
      changes: [{ op: 'insert', table: 'clock_rows', values: { id: 3, value: 'current' } }],
    };
    expect(await received(current)).toMatchObject({ state: 'confirmed', conflicts: [] });
    const older: Transaction = {
      ...current, id: id(), hlc: { ...current.hlc, physicalMilliseconds: current.hlc.physicalMilliseconds - 1000 },
      changes: [{ op: 'update', table: 'clock_rows', key: { id: 3 }, values: { value: 'older' } }],
    };
    const lost = await received(older);
    expect(lost).toMatchObject({ state: 'confirmed', conflicts: [{ kind: 'superseded' }] });
    await expect.poll(supersededCount).toBe(baseline + 1);
    expect(await received(older)).toEqual(lost);
    const transaction: Transaction = {
      id: id(), region: first.region,
      hlc: { physicalMilliseconds: Date.now() + 86_400_000, logical: 7, nodeId: first.name },
      changes: [{ op: 'insert', table: 'clock_rows', values: { id: 1, value: 'delivered' } }],
    };
    const applied = await received(transaction);
    expect(applied).toMatchObject({ id: transaction.id, state: 'confirmed', conflicts: [] });
    expect(await received(transaction)).toEqual(applied);
    await expect.poll(async () => {
      const ahead = await metric(node, 'terrabase_clock_hlc_ahead_milliseconds');
      const capped = await metric(node, 'terrabase_clock_observe_capped_total');
      return capped >= 1 && ahead <= 50;
    }, { message: 'the receiver reports capped observations and a bounded HLC' }).toBe(true);
    expect(await supersededCount()).toBe(baseline + 1);
    const at = Date.now() - 5_000;
    const change = (row: number, physicalMilliseconds: number, op: string, values?: Record<string, unknown>): Transaction => ({
      id: id(), region: first.region, hlc: { physicalMilliseconds, logical: 0, nodeId: first.name },
      changes: [op === 'insert' ? { op, table: 'clock_rows', values: { id: row, ...values } } : { op, table: 'clock_rows', key: { id: row }, ...(values ? { values } : {}) }],
    });
    for (const row of [4, 5]) {
      expect(await received(change(row, at, 'insert', { value: 'inserted' }))).toMatchObject({ state: 'confirmed', conflicts: [] });
    }
    expect(await received(change(4, at + 200, 'delete'))).toMatchObject({ state: 'confirmed', conflicts: [] });
    expect(await received(change(4, at + 100, 'update', { value: 'older than the delete' })))
      .toMatchObject({ state: 'confirmed', conflicts: [{ kind: 'superseded' }] });
    await expect.poll(supersededCount, { message: 'an update older than the tombstone is superseded and counted' }).toBe(baseline + 2);
    expect(await received(change(5, at + 100, 'delete'))).toMatchObject({ state: 'confirmed', conflicts: [] });
    expect(await received(change(5, at + 200, 'update', { value: 'newer than the delete' })))
      .toMatchObject({ state: 'confirmed', conflicts: [{ kind: 'superseded' }] });
    await expect.poll(supersededCount, { message: 'an update newer than the tombstone is superseded and counted' }).toBe(baseline + 3);
    expect((await sql(node, "INSERT INTO clock_rows VALUES (2, 'origin still writes')")).status).toBe(200);
    const listed = await sql(node, 'SELECT id, value FROM clock_rows ORDER BY id');
    expect(JSON.parse(listed.body).results[0].rows).toEqual([[1, 'delivered'], [2, 'origin still writes'], [3, 'current']]);
    observations.push('received transactions published into the origin stream applied once each, replays answered with the recorded result');
  } finally {
    await finish(info);
  }
});
