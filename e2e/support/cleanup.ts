export type CleanupStep = () => Promise<unknown>;

export type DeleteResult = { readonly ok: true } | { readonly ok: false; readonly status?: number | undefined; readonly error: { readonly message: string } };

export async function deleted(what: string, result: Promise<DeleteResult>): Promise<void> {
  const answer = await result;
  if (answer.ok || answer.status === 404) return;
  throw new Error(`${what} was not deleted (${answer.status ?? 'no status'}): ${answer.error.message}`);
}

export async function cleanUpAll(steps: Iterable<CleanupStep | false | null | undefined>): Promise<void> {
  const failures: unknown[] = [];
  for (const step of steps) {
    if (!step) continue;
    try {
      await step();
    } catch (error) {
      failures.push(error);
    }
  }
  if (failures.length === 1) throw failures[0];
  if (failures.length > 1) {
    throw new AggregateError(
      failures,
      failures.map((error) => (error instanceof Error ? error.message : String(error))).join('; '),
    );
  }
}
