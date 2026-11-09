---
title: Go quickstart
description: Create tables, insert rows and query typed rows with the Go SDK.
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Go quickstart

Run a complete SQL workflow against your TerraScale database in about five minutes: create a table, insert rows with parameters, read typed rows back, run a multi-statement batch and handle a refused statement.

## Prerequisites

- Go 1.25 or newer
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/fr/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

The SQL methods live on `Database` in the module; the latest tag carries them:

```bash
mkdir quickstart && cd quickstart
go mod init example.com/quickstart
go get terrascale.tech/sdk@v0.0.27
```

The root module is enough: it pulls in the `v1` and `tsauth` packages the quickstart imports.

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
	db := client.Database(project, databaseName)

	// One statement, one transaction. IF NOT EXISTS makes the quickstart safe to run twice.
	if _, err := db.Execute(ctx, "CREATE TABLE IF NOT EXISTS people (id TEXT PRIMARY KEY, name TEXT NOT NULL)"); err != nil {
		log.Fatal(err)
	}

	// Insert with parameters; v1.NewID() mints a ULID primary key.
	ada := tb.NewID()
	if _, err := db.Execute(ctx, "INSERT INTO people (id, name) VALUES (?, ?)", ada, "Ada"); err != nil {
		log.Fatal(err)
	}

	// Select into typed rows: QueryAs maps each row as a JSON object keyed by column name.
	type person struct {
		ID   string `json:"id"`
		Name string `json:"name"`
	}
	people, err := tb.QueryAs[person](ctx, db, "SELECT id, name FROM people WHERE id = ?", ada)
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println("query matched", people[0].ID, people[0].Name)

	// A batch of 1 to 256 statements runs as one transaction: all commit or none do.
	grace := tb.NewID()
	results, err := db.Batch(ctx, []tb.Statement{
		{SQL: "INSERT INTO people (id, name) VALUES (:id, :name)", Named: map[string]any{"id": grace, "name": "Grace"}},
		{SQL: "SELECT count(*) AS n FROM people"},
	})
	if err != nil {
		log.Fatal(err)
	}
	var n int64
	if err := results[1].Rows[0].Scan(&n); err != nil {
		log.Fatal(err)
	}
	fmt.Println("batch inserted, people count:", n)

	// A refused statement returns *tb.SQLError; the whole request rolled back.
	_, err = db.Execute(ctx, "INSERT INTO people (id, name) VALUES (?, 'dup')", ada)
	var sqlErr *tb.SQLError
	if !errors.As(err, &sqlErr) {
		log.Fatal(err)
	}
	fmt.Printf("refused: %s statement %d sqlstate %q\n", string(sqlErr.Code), sqlErr.Statement, sqlErr.SQLState)
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

Expected output (ids vary):

```text
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ada
batch inserted, people count: 2
refused: primary_key_violation statement 0 sqlstate "23505"
```

## How it works

- **The key never reaches TerraBase.** `tsauth.NewAPIKeySource` exchanges it with ts-auth for a short-lived project access token; the client refreshes it automatically. Keep the key on your server; see [authentication](/fr/guides/authentication).
- **Every call is one request and one transaction.** `Execute`, `Query` and `Batch` each post to `/sql/execute`; a batch commits all of its statements or none, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. See [SQL over HTTP](/fr/guides/sql-http).
- **Arguments are parameters, not string interpolation.** Positional `?` values and named (`:name`) values are sent as typed JSON; blobs (`[]byte`) travel as base64. `QueryAs[T]` maps rows by column name into your struct.
- **Refused statements are `*tb.SQLError`.** It carries `Code`, `Statement` (zero-based), `SQLState` and `Status`, and wraps the API error so `errors.Is` matches every code constant. A `serialization_conflict` is resent at most twice; nothing else is.

## Next steps

- [SDK reference](/fr/guides/sdks) — the five SDKs and their install commands
- [JavaScript](/fr/getting-started/javascript), [C#](/fr/getting-started/csharp) and [Python](/fr/getting-started/python) quickstarts
- [SQL over HTTP](/fr/guides/sql-http) for the raw HTTP interface and [concepts](/fr/concepts/sql) for the SQL dialect

</div>
