# Fast and Slow test budgets

`npm test` runs the parallel Fast HTTP, WebSocket and framework journeys.
`npm run test:slow` retains independent package build, pack, consumer installation,
declaration compilation and runtime qualification. The release qualification
script runs both suites before packing its release artifact.

The complete packed-consumer journey moved unchanged to Slow for #1084 after
an admitted dev-host run measured 19.783 seconds. Fast retains the public SDK
happy path, authentication/refusal, isolation and retry contract coverage.
Fast has 51 cases after the move (52 before); no test was deleted or newly skipped.
