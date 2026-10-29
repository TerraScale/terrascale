# ts-python

Follow [workspace Shipping code](/home/mariogk/Projects/TerraScale/AGENTS.md#shipping-code-user-2026-10-29-go-fast-break-things-fast-q-trunk-q-local-check).
TerraScale Python SDK (`terrascale` on PyPI). Sync client: `src/terrascale/client.py`; asyncio twin: `aio.py`. Both use request descriptions from `_spec.py`. Keep their surfaces identical (`tests/test_contract.py`).

- New routes must exist in `contracts/openapi-routes.json`; refresh with `python scripts/refresh-contract.py`.
- Versions stay `0.0.X` in `src/terrascale/_version.py`.
- Install: `python -m pip install -e '.[dev]'`; build: `python -m build` (install `build` first).
- Fast: `pytest -q` (HTTP fakes in `tests/fake.py`).
- Static checks: `ruff check src tests` and `mypy`.
