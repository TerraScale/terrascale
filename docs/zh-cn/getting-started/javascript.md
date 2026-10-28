---
title: JavaScript quickstart
description: Install the published TerraScale SDK and create, read, update, query and delete a TerraBase document.
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# JavaScript quickstart

Install `@terrascale/sdk@0.0.17` and run one complete document journey against `https://api.terrascale.tech`. You need a database and an API key from the dashboard; [Getting started](/zh-cn/getting-started/#create-an-account-a-database-and-an-api-key) walks through creating them.

## 1. Install the SDK

Use Node.js **22 or 24** and npm **11 or newer**, matching the published package's engine requirements. TypeScript declarations ship with the JavaScript package. Its declared browser targets are the latest major Chrome, Firefox, Safari and Edge; the server example below does not qualify browser authorization.

```bash
mkdir terrabase-quickstart
cd terrabase-quickstart
npm init -y
npm install --save-exact @terrascale/sdk@0.0.17
```

The package is available on [public npm registry](https://registry.npmjs.org/@terrascale/sdk/0.0.17). Do not substitute the old unpublished `0.3.0` starter pin.

## 2. Supply connection details

Use Bash, `curl` and `jq` with shell tracing off. Exchange your dashboard API key for a 60-second access token (`expires_in: 60`), then set the project ID (dashboard **Organization** page, **Current project**), the database name and a collection name. The example creates the collection when it does not exist yet. It reads environment variables; it does not load an `.env` file.

```bash
set +x
set -o pipefail
read -rs TERRASCALE_API_KEY &&  # paste the key, then press Enter
TERRASCALE_ACCESS_TOKEN="$(
  printf 'user = "%s:%s"\n' "${TERRASCALE_API_KEY%%_*}" "$TERRASCALE_API_KEY" |
    curl -sS --fail-with-body --config - -d grant_type=client_credentials \
      https://auth.terrascale.tech/terrascale/oauth/token |
    jq -er '.access_token | select(type == "string" and length > 0)'
)" && export TERRASCALE_ACCESS_TOKEN
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
export TERRASCALE_COLLECTION_NAME='posts'
```

The key is the client secret and its part before `_` is the client ID; sending the key itself as a bearer token answers `401`. Run the exchange again when the token expires. Never place the key or a token in browser public settings, source control or screenshots. See [authentication](/zh-cn/guides/authentication).

## 3. Connect through the SDK

Download [quickstart.mjs](/examples/terrabase-js/quickstart.mjs) into this directory. `@terrascale/sdk` is ESM-only, so the example is an ES module (`.mjs`) and uses `import`. `require("@terrascale/sdk")` fails with `ERR_PACKAGE_PATH_NOT_EXPORTED`; from CommonJS code, call `const { createTerraBaseV1Client } = await import("@terrascale/sdk")` inside an async function. The connection uses the public package entrypoint:

<<< @/../examples/terrabase-js/quickstart.mjs#connection{js}

`token` is the access token from step 2. The client sends it as `Authorization: Bearer` on every request.

Before the journey, the example creates the collection and accepts `409` when it already exists:

<<< @/../examples/terrabase-js/quickstart.mjs#collection{js}

## 4. Create, read, update, query and delete

The downloaded example executes this journey. Each write carries a version precondition where it has an existing version, and each request shares a 60-second cancellation deadline:

<<< @/../examples/terrabase-js/quickstart.mjs#journey{js}

`patch` with `merge` follows JSON Merge Patch: a `null` member removes that field. Use `put` to replace the complete document and `ifNoneMatchAny: true` for create-only writes with a caller-chosen ULID. Returned versions are opaque; copy them unchanged.

Run:

```bash
node quickstart.mjs
```

Success prints `PASS_TERRABASE_CRUD_QUERY`. The assertions verify persisted fields, stale-write rejection, query contents and deletion. The example deletes only its own generated document. It does not delete the database or collection. An interrupted mutation can leave data behind; [reconcile its outcome](/zh-cn/guides/errors) before retrying or cleaning it up.

## 5. Continue safely

For a long-running server, use the complete [renewing token provider](/examples/server-token-provider/README.txt), which pins published SDK 0.0.17.

Generate [TypeScript document models](/zh-cn/guides/schema-validation#generate-typescript-document-models) from a declared collection schema.

A query page may contain fewer results than its limit, or no results with a continuation cursor. Use [pagination](/zh-cn/guides/queries) to continue until `nextCursor` is `null`. Review [errors and retries](/zh-cn/guides/errors) before adding automatic retries.

</div>
