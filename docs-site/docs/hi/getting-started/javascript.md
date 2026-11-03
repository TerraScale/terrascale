---
title: JavaScript quickstart
description: Create, read, update and query TerraBase documents with the JavaScript SDK.
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# JavaScript quickstart

Run a complete document workflow against your TerraScale database in about five minutes: create a collection, write a document, read it back, update it, query it and delete it.

## Prerequisites

- Node.js 22 or 24
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/hi/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

```bash
mkdir quickstart && cd quickstart
npm init -y
npm install @terrascale/sdk
```

## Run the quickstart

Save this as `quickstart.mjs`. The program exchanges the key for a short-lived access token itself; every other operation goes through the client.

```js
import { createTerraBaseV1Client, TerraBaseV1Error, v1Filter } from "@terrascale/sdk";
import { createTsAuthClient } from "@terrascale/sdk/ts-auth";

const required = (name) => {
  const value = process.env[name];
  if (!value) throw new Error(`Missing ${name}`);
  return value;
};

const project = required("TERRASCALE_PROJECT_ID");
const apiKey = required("TERRASCALE_API_KEY");
const databaseName = required("TERRASCALE_DATABASE_NAME");

// The SDK ships no key exchange: exchange the dashboard key for a short-lived
// project access token (60 seconds) here and hand the client a token provider.
const auth = createTsAuthClient({
  apiBaseUrl: "https://auth.terrascale.tech",
  instanceSlug: "terrascale",
});
const separator = apiKey.indexOf("_");
if (separator < 1) throw new Error("Expected a dashboard API key.");
const keyId = apiKey.slice(0, separator);
let cached;
let expiresAt = 0;
async function getAccessToken() {
  if (cached !== undefined && expiresAt - performance.now() > 5000) return cached;
  const result = await auth.oidc.token(keyId, apiKey, {
    grant_type: "client_credentials",
    project,
    scope: "documents:read documents:write",
  });
  if (!result.ok) throw new Error("Token exchange failed; check the key's permissions.");
  cached = result.value.access_token;
  expiresAt = performance.now() + result.value.expires_in * 1000;
  return cached;
}

const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project,
  token: getAccessToken,
});
const database = client.database(databaseName);

// A new database has no collections; 409 name_taken means the collection already exists.
try {
  await database.collections.create("todos");
} catch (error) {
  if (!(error instanceof TerraBaseV1Error && error.status === 409)) throw error;
}
const todos = database.collection("todos");

// Write a document under a server-generated id.
const created = await todos.create({ title: "Ship the quickstart", done: false });
console.log("created", created.id, "at version", created.version);

// Read it back.
const doc = await todos.get(created.id);
console.log("read", doc.id, doc.document.title);

// Update it conditionally: ifMatch rejects the write when another writer moved the document on.
const updated = await todos.patch(created.id, { merge: { done: true } }, { ifMatch: doc.version });
console.log("patched at version", updated.version);

// Query for the change just written.
for await (const item of todos.items({ filter: v1Filter.eq("/done", true), limit: 10 })) {
  console.log("query matched", item.id, item.document.title);
}

// Delete it.
await todos.delete(created.id, { ifMatch: updated.version });
console.log("deleted", created.id);
```

Run it with the key exported in your shell:

```bash
node quickstart.mjs
```

Expected output (ids and versions vary):

```text
created 01JDV7QZGK0DMB3W3QV2AB5CDM at version tbv1-22c40936cb275b5fd9e31f293a3883e8
read 01JDV7QZGK0DMB3W3QV2AB5CDM Ship the quickstart
patched at version tbv1-11c4ca9f65a4729652a1e907835fd792
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ship the quickstart
deleted 01JDV7QZGK0DMB3W3QV2AB5CDM
```

## How it works

- **The key never reaches TerraBase.** The program exchanges it with ts-auth for a 60-second project access token; `token: getAccessToken` hands the client a fresh one whenever it needs it. Keep the key on your server; see [authentication](/hi/guides/authentication).
- **Collections are created explicitly.** `collections.create` answers `409` when the name exists, which makes the quickstart safe to run twice.
- **Versions are opaque concurrency tokens.** Pass the version from a read into `ifMatch` on the next write; TerraBase answers `412` when the document changed in between. See [errors and safe retries](/hi/guides/errors).
- **Documents are JSON.** Document ids are canonical ULIDs; `v1Filter` paths are JSON Pointers such as `/done`.

## Next steps

- [SDK reference](/hi/guides/sdks) — the five SDKs and their install commands
- [C#](/hi/getting-started/csharp), [Go](/hi/getting-started/go) and [Python](/hi/getting-started/python) quickstarts
- [SQL over HTTP](/hi/guides/sql-http) for the SQL interface

</div>
