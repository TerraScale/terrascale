// Export is intentionally retained as a non-runnable fixture until producer
// qualification completes. The typed SDK operation is already available.
import { strict as assert } from "node:assert";

export const runnable = false;
export const title = "Logical database export";

export async function run(): Promise<void> {
  assert.fail("The typed exports producer is pending qualification.");
}
