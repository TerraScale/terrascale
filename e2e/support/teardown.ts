import { onLocalStack, r2StandIn, stripeStandIn } from './stack';
import { reportSignupCounts } from './signup-counts';

export default async function teardown(): Promise<void> {
  await onLocalStack(async () => {
    await reportSignupCounts();
    for (const [name, standIn] of [['Stripe', stripeStandIn], ['R2', r2StandIn]]) {
      const log = (await (await fetch(`${standIn}/log`)).json()) as { unexpected: unknown[] };
      if (log.unexpected.length > 0) throw new Error(`the ${name} stand-in refused ${log.unexpected.length} unexpected calls: ${JSON.stringify(log.unexpected)}`);
    }
  });
}
