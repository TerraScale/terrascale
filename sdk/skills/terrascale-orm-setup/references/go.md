# Go: GORM, sqlc and bun

Source: [Go ORM guide](https://docs.terrascale.tech/guides/orm-go).

## 1. Install and connect

~~~sh
go get terrascale.tech/sdk
~~~

The [Go quickstart](https://docs.terrascale.tech/getting-started/go) pins v0.0.27.
Supply the project ID and API key through the environment. In your application's
main function, construct the database/sql handle with error handling:

~~~go
tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
    Key: os.Getenv("TERRASCALE_API_KEY"),
    Project: os.Getenv("TERRASCALE_PROJECT_ID"),
})
if err != nil {
    log.Fatal(err)
}
client, err := tb.New(tb.Config{TokenSource: tokens})
if err != nil {
    log.Fatal(err)
}
db := client.Database(os.Getenv("TERRASCALE_PROJECT_ID"), os.Getenv("TERRASCALE_DATABASE_NAME"))
sqlDB := tb.OpenDB(db)
defer sqlDB.Close()
~~~

Imports: log, os, tb alias for terrascale.tech/sdk/v1, and
terrascale.tech/sdk/tsauth. There is no local database file or driver DSN in this
path. Each statement outside a transaction is its own request; database/sql
transactions use one SDK SQL session.

## 2. Choose the query layer and schema owner

### GORM

~~~sh
go get gorm.io/gorm gorm.io/driver/sqlite
~~~

Import gorm.io/gorm and gorm.io/driver/sqlite, then use the remote handle:

~~~go
gormDB, err := gorm.Open(sqlite.Dialector{Conn: sqlDB}, &gorm.Config{})
if err != nil {
    log.Fatal(err)
}
type User struct {
    ID string `gorm:"primaryKey"`
    Name string
}
~~~

For an ORM-owned prototype call gormDB.AutoMigrate(&User{}), check its error,
then create User{ID: tb.NewID(), Name: "Ada"}. With ts-owned production history,
create the equivalent users table in migrations and omit runtime AutoMigrate.
The supplied Conn ensures queries go through the SDK; the dialector does not
open a local file. GORM transactions map to one server transaction.

### sqlc

Install sqlc following [its installation guide](https://docs.sqlc.dev/en/stable/overview/install.html).
Configure the generator:

~~~yaml
version: "2"
sql:
  - engine: "sqlite"
    queries: "queries.sql"
    schema: "schema.sql"
    gen:
      go:
        package: "queries"
        out: "queries"
~~~

Define schema.sql with SQLite-dialect CREATE TABLE statements and queries.sql
with sqlc named queries. Run sqlc generate; the generated queries.New(sqlDB)
uses the SDK driver. sqlc generates bindings, not deployed schema: let ts migrate
own application and keep schema.sql aligned with that history.

### bun

~~~sh
go get github.com/uptrace/bun github.com/uptrace/bun/dialect/sqlitedialect
~~~

Import those packages, wrap sqlDB using bun.NewDB(sqlDB, sqlitedialect.New()),
and define a model with bun.BaseModel tagged bun:"table:notes", an ID string
tagged bun:",pk", and a Body string. The guide's NewCreateTable().Model(...)
can own a disposable prototype schema. For ts ownership, omit runtime creation
and migrate an equivalent notes table first. NewInsert().Model(...) sends the
write; RunInTx maps to one server transaction.

## 3. Verify limits

- [ ] Read a parameterized row back through the chosen layer.
- [ ] Check transaction errors and keep work inside the five-second limit.
- [ ] Ensure model tables and deployed SQL match under the chosen owner.

The dialect is SQLite with SQLite type affinity and SQL API refusals.
Each open transaction holds one session; each connection runs one transaction
at a time. LISTEN/NOTIFY are unavailable.
Sources: [Go ORM guide](https://docs.terrascale.tech/guides/orm-go),
[SQL](https://docs.terrascale.tech/concepts/sql),
[limits](https://docs.terrascale.tech/limits).
