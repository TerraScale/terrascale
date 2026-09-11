# D-47: `make check` is the verification set, exactly what CI runs.
check:
	npm ci && npm run check && npm run lint

.PHONY: check
