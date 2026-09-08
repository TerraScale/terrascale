export const title = "Database per tenant";
// The tenant provisioning producer route is not published yet. Keep this
// fixture beside the docs, but do not document an invented endpoint.
export const runnable = false;

export async function run(): Promise<void> {
  throw new Error("Tenant provisioning is not published yet");
}
