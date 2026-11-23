# ts-python

Read the root [AGENTS.md](../../AGENTS.md).

TerraScale Python SDK (`terrascale` on PyPI). Run area commands from `sdk/python/` in the monorepo.

## Layout and checks

- Sync client: `src/terrascale/client.py`; asyncio twin: `aio.py`. Both use request descriptions from `_spec.py`; keep their surfaces identical.
- New routes must exist in `contracts/openapi-routes.json`; refresh with `python scripts/refresh-contract.py`.
- Versions stay `0.0.X` in `src/terrascale/_version.py`.
- Install: `python -m pip install -e '.[dev]'`; build: `python -m build`.
- Static checks: `ruff check src` and `mypy`. From the repository root: `make sdk-python-check`.
