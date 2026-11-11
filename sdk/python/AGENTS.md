# ts-python

Run area commands from `sdk/python` in the monorepo.

TerraScale Python SDK (`terrascale` on PyPI). Sync client: `src/terrascale/client.py`; asyncio twin: `aio.py`. Both use request descriptions from `_spec.py`. Keep their surfaces identical.

- New routes must exist in `contracts/openapi-routes.json`; refresh with `python scripts/refresh-contract.py`.
- Versions stay `0.0.X` in `src/terrascale/_version.py`.
- Install: `python -m pip install -e '.[dev]'`; build: `python -m build` (install `build` first).
- No fakes, demo stores or skip-auth. The journey in `e2e/` is the only test; an SDK operation gets a step in its runners (`e2e/AGENTS.md`).
- Static checks: `ruff check src` and `mypy`. From the repository root, `make sdk-python-check` runs the locked install and `mypy`.
