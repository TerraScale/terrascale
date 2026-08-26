import { bootstrapE2E, writeRuntimeFile } from "./runtime";

export default async function globalSetup(): Promise<void> {
  const runtime = await bootstrapE2E();
  writeRuntimeFile(runtime);
  process.stdout.write(
    `[e2e] studio ${runtime.studioUrl} (resync ${runtime.studioResyncUrl}) fixture ${runtime.fixtureUrl} (resync ${runtime.fixtureResyncUrl})\n`,
  );
}
