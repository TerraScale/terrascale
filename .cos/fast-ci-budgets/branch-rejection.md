# Branch timing-budget rejection

Proof branch: `wrk/fast-budget-rejection` at `6ad47b6e897c76053166b7d63caf6e30a26d4341`.

A disposable change added `await Task.Delay(TimeSpan.FromSeconds(11))` to `Healthz_reports_ok_and_the_running_version`. It is absent from the implementation branch and was not pushed to main.

`ts-ship` on mariogk-dev ran all five vendored-driver tests and all 79 TerraBase Fast tests. Every test passed functionally. The retained timing report included:

```text
11.276s Healthz_reports_ok_and_the_running_version
FAILED Fast test exceeded 10s
RED terrascale terrabase FAILED Fast test exceeded 10s /home/mariogk/.local/state/ts-ship/20261104T121756-ae61a9ea.log
```

The independent Fast execution clock was 27.544s; compilation was 380.967s. The failure was the individual-test budget, not the suite deadline.

GitHub status: `ts-ship/terrabase` = `failure`, `Fast failed in 27.544s (exit 2); build 380.967s`.

`git merge-base --is-ancestor 6ad47b6e897c76053166b7d63caf6e30a26d4341 origin/main` returned 1. The failed candidate's hidden shipping ref was cleaned up.
