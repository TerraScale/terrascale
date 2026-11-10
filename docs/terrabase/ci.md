# TerraBase CI

A pull request must pass these checks from `.github/workflows/ci.yml`:

- `changes` selects the areas the change touches.
- `checks` runs the policy checks that sit beside `changes`.
- `fast` is the Fast suite for each selected area (`libs`, `auth`, `terrabase`).

The Slow suite is not required on every pull request. `.github/workflows/slow.yml` runs it on `main` on a schedule when its inputs changed.

The power-loss end-to-end tests run nightly on `main` from `.github/workflows/terrabase-power-loss.yml` and are not part of every pull request.

Local Fast validation is `make terrabase-test-fast`. Local Slow validation is `make terrabase-test-slow`.
