# Support Node.js 22 and 24

Status: Accepted.

Date: 2026-08-29

## Context

The SDK runs in server applications and browser bundles. Release qualification
needs a small, maintained runtime window without directing consumers to preview
JavaScript releases.

## Decision

Support Node.js 22 and 24 for server use and the current stable major versions
of Chrome, Firefox, Safari, and Edge for browser use. Pin the release builder,
Node binaries, npm version, and Playwright engine revisions in the workflow.
Do not claim a browser or runtime passed when its lane was skipped or
host-blocked.

## Consequences

`package.json` declares the Node and browser ranges. The release workflow runs
Node 22/24 and browser-engine lanes. A failed or skipped required lane blocks
publication.
