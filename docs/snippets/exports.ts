// Export is intentionally retained as a non-runnable fixture until the
// published SDK exposes the typed exports operation (contract gap).
import { strict as assert } from "node:assert";

export const runnable = false;
export const title = "Logical database export";

export async function run(): Promise<void> {
  assert.fail("The typed exports operation is not published yet.");
}
