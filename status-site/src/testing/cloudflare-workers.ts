/* Stands in for the workerd-only "cloudflare:workers" module under Vitest. */

export class DurableObject<Env = unknown> {
  constructor(
    protected readonly ctx: DurableObjectState,
    protected readonly env: Env,
  ) {}
}
