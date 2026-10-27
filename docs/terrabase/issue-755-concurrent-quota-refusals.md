# Concurrent quota refusal assertions (#755)

An in-flight cross-region create can return either the exhausted-plan message or
the documented temporary allowance-elsewhere message under the same HTTP 409
`database_limit` code. A peer transfer has a two-second deadline; during concurrent
creates the local catalog may not yet show all databases that consumed units.
Treating every in-flight refusal as proven global exhaustion made the test flaky.

The Slow test now validates those two exact messages during the concurrent phase,
retaining the exact Created/Conflict counts, JSON/no-store envelope, complete ID
sets in all three regions, all three Starter rounds and the Hobby round.
After each region's catalog contains every successful database, the test issues
another create in each region and requires the exact exhausted-plan message and
unchanged ID set. This keeps the stronger assertion at its actual precondition.
The deadline quarantine and its skip attribute are removed. No production behavior,
peer deadline, parallelism or retry policy changed.
