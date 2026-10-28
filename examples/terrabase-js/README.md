# JavaScript TerraBase example

Install Node 22 or 24 and npm 11 or newer, then run `npm ci` in this directory.
This consumer pins the published `@terrascale/sdk@0.0.17` package.

Create a database and an API key in the dashboard, exchange the key for an
access token and export `TERRASCALE_API_URL=https://api.terrascale.tech`,
`TERRASCALE_PROJECT_ID`, `TERRASCALE_DATABASE_NAME`,
`TERRASCALE_COLLECTION_NAME` and `TERRASCALE_ACCESS_TOKEN` as shown in
https://docs.terrascale.tech/getting-started/javascript. The example creates the
collection when it does not exist. Keep the key and token in your secret
manager or private shell.

Run `npm start`. Success prints `PASS_TERRABASE_CRUD_QUERY`. The example
verifies CRUD, a version conflict, paginated query contents and deletion of
its own generated document. An interrupted mutation may leave that document
behind; reconcile the outcome before retrying. The example does not print
credentials, raw error messages or document bodies.

Local transport tests use synthetic responses only and do not qualify a
hosted service, production JWT authorization or multi-region behavior.
