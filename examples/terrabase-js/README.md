# JavaScript TerraBase example

Install Node 22 or 24 and npm 11 or newer, then run `npm ci` in this directory.
This consumer pins the published `@terrascale/sdk@0.0.3` package.

Supply `TERRASCALE_API_URL`, `TERRASCALE_PROJECT_ID`,
`TERRASCALE_DATABASE_NAME`, `TERRASCALE_COLLECTION_NAME` and
`TERRASCALE_ACCESS_TOKEN` from an authorized isolated development allocation.
Keep the credential in your secret manager or private shell.

Run `npm start`. Success prints `PASS_TERRABASE_CRUD_QUERY`. The example
verifies CRUD, a version conflict, paginated query contents and deletion of
its own generated document. An interrupted mutation may leave that document
behind; reconcile the outcome before retrying. The example does not print
credentials, raw error messages or document bodies.

Local transport tests use synthetic responses only and do not qualify a
hosted service, production JWT authorization or multi-region behavior.
