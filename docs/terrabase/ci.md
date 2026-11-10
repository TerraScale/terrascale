# TerraBase CI

A pull request must pass the `all-green` check of `.github/workflows/ci.yml`, which needs these jobs:

- `changes` selects the areas the change touches.
- `checks` runs the policy checks that sit beside `changes`.
- `libs`, `auth` and `terrabase` build each selected area and run its Fast suite; `terrabase` also runs the local-only end-to-end category through `make terrabase-e2e-local`.
- `dashboard`, `sdk`, `docs`, `landing` and `status` run the Fast checks of the dashboard, of each selected SDK and of the docs, landing and status sites.
- `test` passes only when `changes`, `checks` and every selected Fast area passed.

No workflow runs the Slow suite.

Local Fast validation is `make terrabase-test-fast`. Local Slow validation is `make terrabase-test-slow`.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
