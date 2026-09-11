# Usage-admission clippy slice

Documentation-only batch from `terrabase-server-clippy-next-current.md`.

Owned selectors addressed:

- `usage_admission.rs:66` (`AllocationBindingResolver::binding`)
- `usage_admission.rs:77` (backticks around `ECDSA P-256 SubjectPublicKeyInfo`)
- `usage_admission.rs:215` (`with_allocation_verifier`)
- `usage_admission.rs:231` (`admit_allocation`)
- `usage_admission.rs:344` (`resolve_allocation`)
- `usage_admission.rs:555` (`resolve_with_residency`)

Only rustdoc changed. Billing registration, allocation signature validation,
and durable journal behavior are unchanged. Cargo verification is deferred to
the sole shared build owner `terrabase_clippy_batch`.
