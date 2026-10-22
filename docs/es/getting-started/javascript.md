---
title: JavaScript quickstart
description: Install the published TerraScale SDK and create, read, update, query and delete a TerraBase document.
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# JavaScript quickstart

Install `@terrascale/sdk@0.0.3` and run one complete document journey. This example uses a provisioned project, database and collection; it does not create account resources or assume public registration is available.

## 1. Install the SDK

Use Node.js **22 or 24** and npm **11 or newer**, matching the published package's engine requirements. TypeScript declarations ship with the JavaScript package. Its declared browser targets are the latest major Chrome, Firefox, Safari and Edge; the server example below does not qualify browser authorization.

```bash
mkdir terrabase-quickstart
cd terrabase-quickstart
npm init -y
npm install --save-exact @terrascale/sdk@0.0.3
```

The package is available on [public npm registry](https://registry.npmjs.org/@terrascale/sdk/0.0.3). Do not substitute the old unpublished `0.3.0` starter pin.

## 2. Supply connection details

Set `TERRASCALE_API_URL`, `TERRASCALE_PROJECT_ID`, `TERRASCALE_DATABASE_NAME` and `TERRASCALE_COLLECTION_NAME` from your authorized development allocation. Supply `TERRASCALE_ACCESS_TOKEN` through your secret manager or private shell. The example reads environment variables; it does not load an `.env` file.

```bash
export TERRASCALE_API_URL='https://YOUR_AUTHORIZED_API_HOST'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='quickstart'
export TERRASCALE_COLLECTION_NAME='posts'
```

Replace the endpoint with the actual provisioned HTTPS origin. These names select customer resources and credentials; they are not feature switches. Never place a privileged credential in browser public settings, source control or screenshots.

The current client requires one explicit endpoint or a caller-supplied resolver. It does not promise automatic region discovery or fallback. See [authentication](/es/guides/authentication).

## 3. Connect through the SDK

Download [quickstart.mjs](/examples/terrabase-js/quickstart.mjs) into this directory. The connection uses the public package entrypoint:

<<< @/../examples/terrabase-js/quickstart.mjs#connection{js}

The `token` must be accepted by the serving API. The published client's acceptance of JWT syntax does not verify deployed JWT signature validation, sign-in or access exchange.

## 4. Create, read, update, query and delete

The downloaded example executes this journey. Each write carries a version precondition where it has an existing version, and each request shares a 60-second cancellation deadline:

<<< @/../examples/terrabase-js/quickstart.mjs#journey{js}

`patch` with `merge` follows JSON Merge Patch: a `null` member removes that field. Use `put` to replace the complete document and `ifNoneMatchAny: true` for create-only writes with a caller-chosen ULID. Returned versions are opaque; copy them unchanged.

Run:

```bash
node quickstart.mjs
```

Success prints `PASS_TERRABASE_CRUD_QUERY`. The assertions verify persisted fields, stale-write rejection, query contents and deletion. The example deletes only its own generated document. It does not delete the database or collection. An interrupted mutation can leave data behind; [reconcile its outcome](/es/guides/errors) before retrying or cleaning it up.

## 5. Continue safely

A query page may contain fewer results than its limit, or no results with a continuation cursor. Use [pagination](/es/guides/queries) to continue until `nextCursor` is `null`. Review [errors and retries](/es/guides/errors) before adding automatic retries. SDK transport checks with synthetic responses are local evidence; a deployed customer journey requires its own authorized real-service run.

</div>
