---
title: Use GORM, sqlc and bun with Go
description: Run GORM, sqlc and bun against your TerraScale database through the Go SDK's database/sql driver.
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Use GORM, sqlc and bun with Go

The Go SDK ships a `database/sql` driver: `v1.OpenDB` wraps a `*v1.Database` in a `*sql.DB`, so every Go tool built on `database/sql` works against your TerraScale database. GORM, sqlc and bun all accept that `*sql.DB` directly. The SQL dialect is SQLite; everything the SQL API refuses (see [SQL](/es/concepts/sql)) is refused through the driver too.

## Install

```bash
go get terrascale.tech/sdk
```

The SQL methods live on `Database` in the module's `v1` and `tsauth` packages.

## Connect

Export your project ID and API key as in the [Go quickstart](/es/getting-started/go), then build the client and wrap the database handle:

```go
package main

import (
	"log"
	"os"

	tb "terrascale.tech/sdk/v1"
	"terrascale.tech/sdk/tsauth"
)

func main() {
	tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
		Key:     os.Getenv("TERRASCALE_API_KEY"),
		Project: os.Getenv("TERRASCALE_PROJECT_ID"),
	})
	if err != nil {
		log.Fatal(err)
	}
	client, err := tb.New(tb.Config{TokenSource: tokens})
	if err != nil {
		log.Fatal(err)
	}
	db := client.Database(os.Getenv("TERRASCALE_PROJECT_ID"), "main")

	sqlDB := tb.OpenDB(db)
	defer sqlDB.Close()
}
```

Outside a transaction, each statement is its own request and commits on its own. `sqlDB.Begin()` opens a transaction over the SDK's SQL session: `Commit` and `Rollback` close it, and every statement in between runs in that one server-side transaction.

## GORM

GORM takes the `*sql.DB` through the `gorm.io/driver/sqlite` dialector's `Conn` field. The dialector only supplies its SQL dialect: with `Conn` set it never opens a local SQLite file, and every statement goes through the SDK.

```sh
go get gorm.io/gorm gorm.io/driver/sqlite
```

```go
import (
	"gorm.io/gorm"
	"gorm.io/driver/sqlite"
)

gormDB, err := gorm.Open(sqlite.Dialector{Conn: sqlDB}, &gorm.Config{})
if err != nil {
	log.Fatal(err)
}

type User struct {
	ID        string `gorm:"primaryKey"`
	Name      string
	CreatedAt time.Time
	UpdatedAt time.Time
	DeletedAt gorm.DeletedAt `gorm:"index"`
}

if err := gormDB.AutoMigrate(&User{}); err != nil {
	log.Fatal(err)
}

user := User{ID: tb.NewID(), Name: "Ada"}
if err := gormDB.Create(&user).Error; err != nil {
	log.Fatal(err)
}

var found User
if err := gormDB.First(&found, "name = ?", "Ada").Error; err != nil {
	log.Fatal(err)
}

err = gormDB.Transaction(func(tx *gorm.DB) error {
	if err := tx.Create(&User{ID: tb.NewID(), Name: "Grace"}).Error; err != nil {
		return err
	}
	return tx.Create(&User{ID: tb.NewID(), Name: "Linus"}).Error
})
```

`gormDB.Transaction` maps to one `Begin`/`Commit` pair, so its whole body runs in one server-side transaction and must finish within the transaction time limit below. GORM's soft-delete queries (`DeletedAt`) and its `time.Time` fields work: the driver writes `time.Time`, `sql.NullTime` and `driver.Valuer` parameters as UTC text and scans `DATE`, `DATETIME` and `TIMESTAMP` columns back into `time.Time`.

## sqlc

sqlc generates type-safe Go from SQL. Set the engine to SQLite so its dialect matches the server's:

```yaml
version: "2"
sql:
  - engine: "sqlite"
    queries: "queries.sql"
    schema: "schema.sql"
    gen:
      go:
        package: "queries"
        out: "queries"
```

The generated `New` takes any `DBTX` — a `*sql.DB`, a `*sql.Tx` or a `*sql.Conn` — so pass the `*sql.DB` from `OpenDB`:

```go
import "example.com/quickstart/queries"

q := queries.New(sqlDB)
users, err := q.ListUsers(ctx)
```

Run `sqlc generate` with the real SQL files; the generated code is plain `database/sql` calls, so it runs through the SDK unchanged.

## bun

bun takes the `*sql.DB` and a dialect:

```sh
go get github.com/uptrace/bun github.com/uptrace/bun/dialect/sqlitedialect
```

```go
import (
	"github.com/uptrace/bun"
	"github.com/uptrace/bun/dialect/sqlitedialect"
)

bunDB := bun.NewDB(sqlDB, sqlitedialect.New())
defer bunDB.Close()

type Note struct {
	bun.BaseModel `bun:"table:notes"`
	ID   string `bun:",pk"`
	Body string
}

if _, err := bunDB.NewCreateTable().Model((*Note)(nil)).IfNotExists().Exec(ctx); err != nil {
	log.Fatal(err)
}

if _, err := bunDB.NewInsert().Model(&Note{ID: tb.NewID(), Body: "hello"}).Exec(ctx); err != nil {
	log.Fatal(err)
}

err = bunDB.RunInTx(ctx, nil, func(ctx context.Context, tx bun.Tx) error {
	note := &Note{ID: tb.NewID(), Body: "in transaction"}
	_, err := tx.NewInsert().Model(note).Exec(ctx)
	return err
})
```

`bunDB.RunInTx` maps to one `Begin`/`Commit` pair over one SQL session.

## Limits

- The dialect is SQLite: SQLite SQL and SQLite type affinity, with the SQL API's own refusals (see [SQL](/es/concepts/sql)).
- A transaction lives at most 5 seconds; past the limit the server rolls it back, so keep transactions small ([limits](/es/limits)).
- Each open transaction holds one SQL session, and a connection runs one transaction at a time.
- `LISTEN`/`NOTIFY` are Postgres features and do not exist in the SQLite dialect.

</div>
