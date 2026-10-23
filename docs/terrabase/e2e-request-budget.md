# E2E admission and retry budget

`tests/TerraBase.E2E/Docs.cs` keeps setup, cleanup and document requests inside
the convergence deadline. `Docs.AdmittedAsync` retries only a 429 whose JSON
`error.code` is `tenant_busy` and whose positive `Retry-After` fits the remaining
budget. Its wait observes caller cancellation.

`tenant_memory`, unknown write outcomes, unreadable bodies and arbitrary 503s
are not automatic retry permission: the request may already have executed.
The separate pre-execution-refusal path accepts only the exact 503 codes
`remote_region_unavailable` and `confirmation_busy`; a message containing a code
or an ingress-generated response is insufficient.

`AdmissionRetryTests` exercises admission retries against a real isolated node.
This documents harness behavior, not a production project's current plan, request
quota or OCI acceptance result. Verify those through the authorized current runtime;
retired account-sizing and proxy recipes are not deployment instructions.
