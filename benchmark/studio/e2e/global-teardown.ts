import { teardownE2E } from "./runtime";

export default async function globalTeardown(): Promise<void> {
  await teardownE2E();
}
