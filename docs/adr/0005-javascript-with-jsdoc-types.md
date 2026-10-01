# Ship JavaScript typed with JSDoc

Status: Accepted.

Date: 2026-10-01

## Context

The SDK was authored in TypeScript and compiled to `dist/`. The published
JavaScript differed from the reviewed source, and every check, script, and
consumer depended on a build step first.

## Decision

Author the SDK, its tests, and its scripts as plain ES modules typed with JSDoc.
`tsc` checks the JavaScript with the same strict options (`checkJs`) and emits
only declarations into `types/`. The package publishes `src/` unchanged
alongside `types/`; package exports map runtime conditions to `src/` and
`types` to `types/`.

## Consequences

The released JavaScript is the reviewed source byte for byte. Node runs scripts
directly. TypeScript consumers keep equivalent declarations. Type syntax lives
in JSDoc (`@typedef`, `@template`, `@import`, `@type` casts); hand-written
`.d.ts` files are not used for source modules. The repository is named `ts-js`.
