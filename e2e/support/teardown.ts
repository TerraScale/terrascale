import { onLocalStack, stripeStandIn } from './stack';

export default async function teardown(): Promise<void> {
  await onLocalStack(async () => {
    const log = (await (await fetch(`${stripeStandIn}/log`)).json()) as { unexpected: unknown[] };
    if (log.unexpected.length > 0) throw new Error(`the Stripe stand-in refused ${log.unexpected.length} unexpected calls: ${JSON.stringify(log.unexpected)}`);
  });
}
