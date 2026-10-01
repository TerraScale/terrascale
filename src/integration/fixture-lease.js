/**
 * Required allocations are externally provisioned; the SDK invents no provisioning route.
 *
 * @returns {never}
 */
export function requireDisposableProducerAllocation() {
  throw new Error(
    "An actual disposable producer allocation is required; C06 provisioning admission remains open.",
  );
}
