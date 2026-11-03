---
title: Go quickstart
description: Create, read, update and query TerraBase documents with the Go SDK.
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Go quickstart

Run a complete document workflow against your TerraScale database in about five minutes: create a collection, write a document, read it back, update it, query it and delete it.

## Prerequisites

- Go 1.24 or newer
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/de/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

```bash
mkdir quickstart && cd quickstart
go mod init example.com/quickstart
go get terrascale.tech/sdk@v0.0.8
```

## Run the quickstart

Save this as `main.go`. The SDK exchanges the API key for short-lived project access tokens automatically.

```go
package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"time"

	tb "terrascale.tech/sdk/v1"
	"terrascale.tech/sdk/tsauth"
)

func main() {
	project := required("TERRASCALE_PROJECT_ID")
	apiKey := required("TERRASCALE_API_KEY")
	databaseName := required("TERRASCALE_DATABASE_NAME")
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()

	// tsauth exchanges the dashboard key for short-lived project access
	// tokens; the client refreshes them automatically.
	tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: apiKey, Project: project})
	if err != nil {
		log.Fatal(err)
	}
	client, err := tb.New(tb.Config{TokenSource: tokens})
	if err != nil {
		log.Fatal(err)
	}
	database := client.Database(project, databaseName)

	// A new database has no collections; name_taken means the collection already exists.
	if _, err := database.CreateCollection(ctx, "todos"); err != nil && !errors.Is(err, tb.ErrNameTaken) {
		log.Fatal(err)
	}
	todos := database.Collection("todos")

	// Write a document under a server-generated id.
	created, err := todos.Create(ctx, map[string]any{"title": "Ship the quickstart", "done": false})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println("created", created.ID, "at version", created.Version)

	// Read it back.
	doc, err := todos.Get(ctx, created.ID)
	if err != nil {
		log.Fatal(err)
	}
	var body struct {
		Title string `json:"title"`
	}
	if err := doc.Decode(&body); err != nil {
		log.Fatal(err)
	}
	fmt.Println("read", doc.ID, body.Title)

	// Update it conditionally: IfMatch rejects the write when another writer moved the document on.
	updated, err := todos.Patch(ctx, created.ID, tb.MergePatch(map[string]any{"done": true}), tb.IfMatch(doc.Version))
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println("patched at version", updated.Version)

	// Query for the change just written.
	page, err := todos.Query(ctx, tb.Query{Filter: tb.Eq("/done", true), Limit: 10})
	if err != nil {
		log.Fatal(err)
	}
	for _, item := range page.Items {
		var result struct {
			Title string `json:"title"`
		}
		if err := item.Decode(&result); err != nil {
			log.Fatal(err)
		}
		fmt.Println("query matched", item.ID, result.Title)
	}

	// Delete it.
	if _, err := todos.Delete(ctx, created.ID, tb.IfMatch(updated.Version)); err != nil {
		log.Fatal(err)
	}
	fmt.Println("deleted", created.ID)
}

func required(name string) string {
	value := os.Getenv(name)
	if value == "" {
		log.Fatalf("Missing %s.", name)
	}
	return value
}
```

Run it with the key exported in your shell:

```bash
go run .
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

- **The key never reaches TerraBase.** `tsauth.NewAPIKeySource` exchanges it with ts-auth for a short-lived project access token; the client refreshes it automatically. Keep the key on your server; see [authentication](/de/guides/authentication).
- **Collections are created explicitly.** `CreateCollection` answers `ErrNameTaken` when the name exists, which makes the quickstart safe to run twice.
- **Versions are opaque concurrency tokens.** Pass the version from a read into `tb.IfMatch` on the next write; TerraBase answers `412` when the document changed in between. See [errors and safe retries](/de/guides/errors).
- **Documents are JSON.** Document ids are canonical ULIDs; filter paths are JSON Pointers such as `/done`. `Decode` keeps numbers as `json.Number`.

## Next steps

- [SDK reference](/de/guides/sdks) — the five SDKs and their install commands
- [JavaScript](/de/getting-started/javascript), [C#](/de/getting-started/csharp) and [Python](/de/getting-started/python) quickstarts
- [SQL over HTTP](/de/guides/sql-http) for the SQL interface

</div>
