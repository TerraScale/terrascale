---
title: Rename a database
description: Change a database's name from the dashboard, the API, the CLI or an SDK.
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Rename a database

A database's name is what connection strings use; its id never changes. Rename it from the dashboard, with `PATCH` on the management API, with the `ts` CLI, or through an SDK. The rename is immediate: the previous name stops resolving and there is no alias or redirect, so update every application that connects by name in the same change.

## Dashboard

Open the database's **Settings** page, enter the new name, type the current name to confirm, and choose **Rename database**. Name-based connection strings must be updated right away; the confirmation step exists because the old name breaks the moment the rename lands.

## API

```
PATCH https://api.terrascale.tech/v1/projects/{project}/databases/{database}
Authorization: Bearer <access token>
Content-Type: application/json
```

Exchange a project API key for the access token first, as in [getting started](/fr/getting-started/#exchange-the-key-for-an-access-token). The request needs the `databases:write` scope. Send only a name:

```bash
curl -sS --fail-with-body -X PATCH --header @/dev/fd/3 \
  -H 'Content-Type: application/json' \
  -d '{"name":"orders-eu"}' \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

The answer is the database record: `id`, `projectId`, `name`, `createdAt`, `homeRegion`, `replicaRegions` and the region that answered. `name` cannot be combined with placement fields (`homeRegion`, `replicaRegions`, `allRegions`) in the same request.

A name starts with a lowercase letter and continues with up to 62 lowercase letters, digits, `_` or `-`. The request must reach the database's home region; a request sent to another region answers `421` with the home to use.

## CLI and SDKs

```sh
ts db rename app orders-eu
```

The CLI accepts the database's name or its immutable id. The SDKs expose the same call: `RenameDatabaseAsync(databaseId, "orders-eu")` in .NET and `client.RenameDatabase(ctx, projectID, databaseID, "orders-eu")` in Go.

## Concurrent renames

Two regions can accept different renames at once. The database keeps its id, data and backups under one of the names, and the response reports what happened: the losing name comes back as `previousName`, with `conflictOf` naming the database that kept it. Rename again to choose a final name.

</div>
