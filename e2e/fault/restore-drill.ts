import { expect } from '@playwright/test';
import { metric, nodeRequest } from './support';

type Drill = {
  at: string; passed: boolean; region: string; member: string; destination: string; recoveryPointAt: string;
  generation: string; targetPosition: number; reachedPosition: number; expectedDigest: string; restoredDigest: string;
};

export async function verifyRestoreDrill(writer: { name: string; region: string; address: string; port: number }): Promise<void> {
  let drill: Drill | undefined;
  await expect.poll(async () => {
    const response = await nodeRequest(writer, '/v1/backup');
    expect(response.status, response.body).toBe(200);
    const files = (JSON.parse(response.body) as { files: { file: string; lastDrill: Drill | null }[] }).files;
    drill = files.find(file => file.file === `v1/_system/nodes/${writer.name}`)?.lastDrill ?? undefined;
    return drill?.passed;
  }, { message: `the ${writer.region} writer restores its catalog from its own production-format backup namespace` }).toBe(true);
  expect(drill).toMatchObject({ region: writer.region, member: writer.name, passed: true });
  expect(drill!.destination).toBe('platform');
  expect(drill!.generation).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
  expect(drill!.reachedPosition).toBe(drill!.targetPosition);
  expect(drill!.expectedDigest).toMatch(/^[0-9A-F]{64}$/);
  expect(drill!.restoredDigest).toBe(drill!.expectedDigest);
  expect(Date.parse(drill!.recoveryPointAt)).toBeLessThanOrEqual(Date.parse(drill!.at));
  await expect.poll(() => metric(writer, 'terrabase_backup_drill_last_success_seconds'),
    { message: 'every platform file has a recorded successful latest drill' }).toBeGreaterThan(0);
  expect(await metric(writer, 'terrabase_backup_drill_failed_files')).toBe(0);
  expect(Number.isFinite(await metric(writer, 'terrabase_backup_recovery_point_age_seconds'))).toBe(true);
  console.log(`restore drill ${writer.region}/${writer.name}: ${drill!.at}, recovery ${drill!.recoveryPointAt}, position ${drill!.reachedPosition}, SHA256 ${drill!.restoredDigest}`);
}
