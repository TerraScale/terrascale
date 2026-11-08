# Zero-configuration operating contract

Adopted 2026-10-09 (TS-795).

- Every feature is always on and every deployed region serves as soon as its
  machines boot. There are no feature flags, region activation steps, deploy
  toggles or kill switches.
- OCI production secrets come from OCI Vault via instance principal; ts-status
  uses Worker secrets and Cloudflare Workers Builds keeps its deployment token.
  Operators never copy production credentials by hand.
- Each value has one correct setting in code. Production exposes no tuning
  knobs. Fakes, demo stores and authentication bypasses belong only in test
  code and test binaries.
- CI rejects new switch-style configuration. A narrow allowance for an
  unavoidable third-party name needs a one-line reason.

## In ts-status

- Every endpoint of an active component is probed; there is no per-endpoint
  switch. A new endpoint shows "Pending launch" until its first passing check,
  then counts on its own.
- "Coming soon" components are catalog data shown to customers, not a probe
  toggle: the catalog is edited in the admin page.
- Missing credentials fail closed: without the admin secrets nobody can sign
  in, and a probe whose secret is missing shows "Not configured", never healthy.
