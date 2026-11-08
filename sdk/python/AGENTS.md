# ts-python

Run area commands from `sdk/python` in the monorepo.

TerraScale Python SDK (`terrascale` on PyPI). Sync client: `src/terrascale/client.py`; asyncio twin: `aio.py`. Both use request descriptions from `_spec.py`. Keep their surfaces identical (`tests/test_contract.py`).

- New routes must exist in `contracts/openapi-routes.json`; refresh with `python scripts/refresh-contract.py`.
- Versions stay `0.0.X` in `src/terrascale/_version.py`.
- Install: `python -m pip install -e '.[dev]'`; build: `python -m build` (install `build` first).
- Fast: `pytest -q` (HTTP fakes in `tests/fake.py`).
- Static checks: `ruff check src tests` and `mypy`.
- Each area's Fast suite must finish in under 30 s wall time; each single test must finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. Keep default parallelism.
- Test only the happy path and critical paths in Fast; move heavy critical tests to Slow and delete everything else. New features add at most one happy-path and one critical-failure test.
