# Native restore exact point HTTP selector

Source snapshot: `3af4504d3f9388691ba8d2903ffa406b1a60feee`

## Implemented

- Native provisioning restore accepts `point_id` as exactly 64 lowercase hex
  characters and forwards `{kind: "point_id", point_id}` to the existing
  `ProvisioningClient` transport.
- Omitted selectors continue to forward `{kind: "latest"}`; `at` continues to
  forward `{kind: "unix_millis", unix_millis}`.
- Native requests reject unknown fields, malformed types/nulls, malformed
  point IDs, and simultaneous `at`/`point_id` before dispatch.
- Restore OpenAPI schema documents the strict selector shape.
- Focused parser tests cover forwarding and rejection cases.
- An HTTP router test drives the real authenticated route and Unix-socket
  `ProvisioningClient`, captures point/latest/at forwarding plus source and
  owner scope, and verifies malformed/exclusive/unknown and foreign requests
  do not dispatch.

## Validation

- `git diff --check` — passed.
- `rustfmt --edition 2024 --check crates/terrabase-server/src/api.rs` — passed.
- Cargo build/test/clippy intentionally not run; reserved for the consolidated
  TerraBase server build owner.
