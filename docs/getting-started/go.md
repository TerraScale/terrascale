---
title: Go quickstart
description: Install the published Go SDK and create, get, query, read SQL, stream changes and delete TerraBase documents.
---

# Go quickstart

Install `terrascale.tech/sdk@v0.0.8` with **Go 1.25 or newer**. The example uses the public API at `https://api.terrascale.tech` and an existing project database.

## 1. Install the published module

```bash
mkdir terrabase-go-quickstart
cd terrabase-go-quickstart
go mod init example.com/terrabase-quickstart
go get terrascale.tech/sdk@v0.0.8
```

The [published module](https://proxy.golang.org/terrascale.tech/sdk/@v/v0.0.8.info) includes the `terrascale.tech/sdk/v1` and `terrascale.tech/sdk/tsauth` packages. Install the root module; `/v1` is the client package's import path.

## 2. Supply your project and API key

Create a database and API key in the [dashboard](https://dash.terrascale.tech/) as described in [Getting started](/getting-started/#create-an-account-a-database-and-an-api-key). This example creates the `go_quickstart` collection. Its key needs `documents:read`, `documents:write` for collection creation, and `databases:read` for SQL. Scope it to the project and database you intend to use. The **Read and write** key from Getting started lacks `databases:read`, so SQL answers `403 forbidden_scope`: under **More access options** choose **Full access** for this key. A **Read only** key cannot run `POST /sql` either, and answers `403 forbidden_scope` for the same reason.

In Bash, enter the key without saving its value in shell history:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ULID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
```

Copy the project ID from **Organization → Current project**. It must be a canonical ULID: 26 uppercase Crockford base32 characters. Database and collection names are names, not ULIDs.

`tsauth.NewAPIKeySource` exchanges the whole `<key ID>_<secret>` credential at `https://auth.terrascale.tech/terrascale/oauth/token`, caches the short-lived access token and refreshes it before expiry. The data client sends that access token as `Authorization: Bearer`; sending the API key itself as a bearer token fails. Keep credentials on your server and out of git. See [authentication](/guides/authentication).

## 3. Create, get, query, run SQL and delete

Save this as `main.go`:

```go
package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"time"

	"terrascale.tech/sdk/tsauth"
	tb "terrascale.tech/sdk/v1"
)

func main() {
	if err := run(); err != nil {
		// Do not print raw server errors, credentials or customer documents.
		fmt.Fprintln(os.Stderr, "Quickstart failed; check credentials, permissions and document state before retrying.")
		os.Exit(1)
	}
}

func run() error {
	project := os.Getenv("TERRASCALE_PROJECT_ID")
	database := os.Getenv("TERRASCALE_DATABASE_NAME")
	if err := tb.ValidateID(project); err != nil {
		return err
	}
	if database == "" {
		return errors.New("TERRASCALE_DATABASE_NAME is required")
	}
	tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
		Key: os.Getenv("TERRASCALE_API_KEY"), Project: project,
	})
	if err != nil {
		return err
	}
	client, err := tb.New(tb.Config{TokenSource: tokens})
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	db := client.Database(project, database)
	if _, err := db.CreateCollection(ctx, "go_quickstart"); err != nil && !errors.Is(err, tb.ErrNameTaken) {
		return err
	}
	if len(os.Args) == 2 && os.Args[1] == "watch" {
		return watchChanges(ctx, db)
	}
	docs := db.Collection("go_quickstart")
	created, err := docs.Create(ctx, map[string]any{"title": "Hello from Go"})
	if err != nil {
		return err
	}
	doc, err := docs.Get(ctx, created.ID)
	if err != nil {
		return err
	}
	if doc.State != tb.StateValue {
		return errors.New("document has no unambiguous value")
	}
	var body struct {
		Title string `json:"title"`
	}
	if err := doc.Decode(&body); err != nil {
		return err
	}
	page, err := docs.Query(ctx, tb.Query{
		Filter: tb.Eq(tb.IDPath, created.ID), Limit: 10,
	})
	if err != nil {
		return err
	}
	rows, err := db.SQL(ctx, "SELECT title FROM go_quickstart LIMIT 10")
	if err != nil {
		return err
	}
	if _, err := docs.Delete(ctx, doc.ID, tb.IfMatch(doc.Version)); err != nil {
		return err
	}
	fmt.Printf("Created, read and deleted one document; query returned %d item(s), SQL %d row(s).\n", len(page.Items), len(rows.Rows))
	return nil
}

func watchChanges(ctx context.Context, db *tb.Database) error {
	for event, err := range db.Changes(ctx, tb.ChangesOptions{
		Cursor: "now", Collections: []string{"go_quickstart"},
	}) {
		if err != nil {
			if errors.Is(err, context.DeadlineExceeded) {
				return nil
			}
			return err
		}
		if event.Type == "change" {
			fmt.Printf("Document change: %s\n", event.Op)
		}
	}
	return nil
}
```

Run it from the same shell:

```bash
go run .
```

`Create` generates a canonical ULID. For a caller-chosen ID, use `tb.NewID()` with `docs.Put(ctx, id, body, tb.IfNoneMatchAny())`. UUIDs, prefixes and lowercase IDs are rejected. Preserve the generated ID when reconciling an interrupted write.

The delete uses the version returned by `Get`; versions are opaque and must be passed back unchanged. A stale version fails with `tb.ErrPreconditionFailed`. The program deletes only its own document and leaves the collection and database intact. A failure before deletion can leave the document behind; [reconcile uncertain writes](/guides/errors) before retrying.

`Query` returns one page. Continue with `page.NextCursor` until it is empty, even when a page has no items, keeping the filter, sort and projection unchanged. `docs.Items` and `docs.Pages` iterate across pages. SQL accepts the read-only `SELECT` subset, with a maximum limit of 1,000 rows and an 8 MiB result bound; it does not execute writes.

## 4. Observe database changes

Run the watcher in one terminal with the same environment, then run `go run .` in a second terminal while the watcher is connected:

```bash
go run . watch
```

The watcher starts at `now`, filters to `go_quickstart` and stops after the program's 60-second deadline. It prints operation names without document bodies or tokens. A quiet stream prints nothing; it is not proof that a write occurred.

For a durable consumer, apply events and save each nonempty `event.Cursor` together, including `cursor` events that carry no document. Supply the saved value as `ChangesOptions.Cursor` on restart, with the same collection filter. Delivery is at least once: deduplicate by `Origin`, `Version`, `Collection` and `ID`. Event documents represent the current value and can be null. Handle `tb.ErrCursorExpired` explicitly; the SDK does not silently restart at `now`. Collection and index metadata are outside the document feed.
