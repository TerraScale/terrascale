# ts-python

TerraScale Python SDK (`terrascale` on PyPI). Sync client in `src/terrascale/client.py`,
asyncio twin in `aio.py`; both send the request descriptions built in `_spec.py`.
Keep the two surfaces identical (tests/test_contract.py checks it).

- Tests use HTTP fakes only (`tests/fake.py`); never download or run a TerraBase binary.
- Every new route must exist in `contracts/openapi-routes.json` (refresh with `scripts/refresh-contract.py`).
- Versions stay 0.0.X. CI (GitHub-hosted) is the gate; no local runs are required before pushing.
- Never log or echo API keys or tokens, including in error messages.
