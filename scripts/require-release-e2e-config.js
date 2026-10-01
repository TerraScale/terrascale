#!/usr/bin/env node
/** This mandatory gate is not an environment-presence or import-only check. */
throw new Error(
  "Release E2E remains blocked: C06/C07 management and security authority, real installed producer allocation, and the deployed authenticated JavaScript account/browser journey are required. Local SQLite, source-only tests and import checks cannot satisfy this gate.",
);
