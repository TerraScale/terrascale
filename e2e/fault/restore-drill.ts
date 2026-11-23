import { expect } from '@playwright/test';
import { metric, type SystemBackup } from './support';

export async function verifyRestoreDrill(peer: { name: string; region: string; address: string; port: number; metricsPort?: number }, checkMetrics = true): Promise<void> {
  let backup: SystemBackup | undefined;
  await expect.poll(async () => {
    const response = await fetch(`http://${peer.address}:${peer.port}/backupz`);
    if (response.status === 503) {
      backup = await response.json() as SystemBackup;
      return backup.status;
    }
    expect(response.status, await response.clone().text()).toBe(200);
    backup = await response.json() as SystemBackup;
    return backup.status;
  }, { message: `${peer.region}/${peer.name} reports system backup continuity`, timeout: 60_000 }).toMatch(/^(ok|pending)$/);
  expect(backup).toMatchObject({ region: peer.region });
  if (backup?.status === 'ok') {
    expect(backup.archivedThrough).toEqual(expect.any(String));
    expect(backup.archivedBytes).toBeGreaterThan(0);
    expect(backup.pendingSegments).toEqual(expect.any(Number));
  }
  if (checkMetrics && peer.metricsPort !== undefined) {
    expect(await metric({ address: peer.address, port: peer.port, metricsPort: peer.metricsPort }, 'terrabase_backup_disabled')).toBe(0);
    expect(Number.isFinite(await metric({ address: peer.address, port: peer.port, metricsPort: peer.metricsPort }, 'terrabase_backup_failures_total'))).toBe(true);
  }
  console.log(`system backup ${peer.region}/${peer.name}: ${backup?.status} through ${backup?.archivedThrough ?? 'none'} (${backup?.archivedBytes ?? 0} bytes)`);
}
