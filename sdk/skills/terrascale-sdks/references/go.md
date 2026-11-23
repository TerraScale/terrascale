# Go

Use Go 1.25+; SQL lives in `terrascale.tech/sdk/v1`. The documented release is
`v0.0.27`. Sources: [Go quickstart](https://docs.terrascale.tech/getting-started/go),
[SDK reference](https://docs.terrascale.tech/guides/sdks).

## Install and configure

In a new application directory:

```sh
go mod init example.com/terrascale-quickstart
go get terrascale.tech/sdk@v0.0.27
```

`tb.New(tb.Config{APIKey: key})` exchanges the key; pass the project explicitly
to `Database(project, name)`. `Config.TokenSource` can use `tsauth.NewAPIKeySource` with
`tsauth.APIKeyConfig{Key: key, Project: project}` as in the public quickstart.
Choose one of `APIKey`, `Token`, `TokenSource`. Newer source adds
`tb.NewFromEnvironment()` (key and optional base URL, not project/region),
`Config.TSAuthBaseURL` and `Config.RegionPin`; `v0.0.27` lacks these. On that source,
custom `BaseURL` with an API key needs `TSAuthBaseURL`. For the published module,
use the documented global origin and explicit constructor shown below.
Sources: [Go](https://docs.terrascale.tech/getting-started/go),
[authentication](https://docs.terrascale.tech/guides/authentication),
[zero-configuration](https://docs.terrascale.tech/guides/zero-configuration).

## Complete small example

Save as `main.go` and run `go run .` with the skill's three environment variables.
This uses the token-source construction documented for the released module.

```go
package main

import (
    "context"
    "errors"
    "fmt"
    "log"
    "os"
    "time"

    "terrascale.tech/sdk/tsauth"
    tb "terrascale.tech/sdk/v1"
)

func main() {
    if err := run(); err != nil {
        log.Fatal(err)
    }
}

func run() error {
    project := required("TERRASCALE_PROJECT_ID")
    tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
        Key: required("TERRASCALE_API_KEY"), Project: project,
    })
    if err != nil { return err }
    client, err := tb.New(tb.Config{TokenSource: tokens})
    if err != nil { return err }
    db := client.Database(project, required("TERRASCALE_DATABASE_NAME"))
    ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
    defer cancel()

    if _, err := db.Execute(ctx, "CREATE TABLE IF NOT EXISTS sdk_people (id TEXT PRIMARY KEY, name TEXT NOT NULL)"); err != nil {
        return err
    }
    id := tb.NewID()
    _, err = db.Batch(ctx, []tb.Statement{
        {SQL: "INSERT INTO sdk_people (id, name) VALUES (?, ?)", Args: []any{id, "Ada"}},
        {SQL: "INSERT INTO sdk_people (id, name) VALUES (:id, :name)", Named: map[string]any{"id": tb.NewID(), "name": "Grace"}},
    })
    if err != nil { return err }

    type person struct {
        ID string `json:"id"`
        Name string `json:"name"`
    }
    people, err := tb.QueryAs[person](ctx, db, "SELECT id, name FROM sdk_people WHERE id = ?", id)
    if err != nil { return err }
    fmt.Println(people[0].Name)

    session, err := db.Connect(ctx)
    if err != nil { return err }
    defer session.Close()
    err = session.Transaction(ctx, func(tx *tb.SQLSession) error {
        rows, err := tx.Query(ctx, "SELECT name FROM sdk_people WHERE id = ?", id)
        if err != nil { return err }
        var name string
        if err := rows[0].Scan(&name); err != nil { return err }
        if name == "Ada" {
            _, err = tx.Execute(ctx, "UPDATE sdk_people SET name = ? WHERE id = ?", "Ada Lovelace", id)
        }
        return err
    })
    if err != nil { return err }

    _, err = db.Execute(ctx, "INSERT INTO sdk_people (id, name) VALUES (?, ?)", id, "duplicate")
    var sqlErr *tb.SQLError
    if !errors.As(err, &sqlErr) || string(sqlErr.Code) != "primary_key_violation" {
        if err == nil { return errors.New("expected duplicate refusal") }
        return err
    }
    fmt.Println(string(sqlErr.Code), sqlErr.Statement, sqlErr.SQLState)
    return nil
}

func required(name string) string {
    value := os.Getenv(name)
    if value == "" { panic("Missing " + name) }
    return value
}
```

Expected: `Ada` then `primary_key_violation 0 23505`. Sources:
[Go](https://docs.terrascale.tech/getting-started/go),
[SQL transactions](https://docs.terrascale.tech/guides/sql-http).

## Methods, results and errors

`Execute(ctx, sql, args ...any)` and `Query` take variadic positional values,
not one slice argument. Expand a prepared `[]any` with `args...`.
For named values in `v0.0.27`, use `Batch` with `tb.Statement.Named`, as above,
or `Execute(ctx, sqlText, sql.Named("name", value))` with a `database/sql` import.
`ExecuteStatement` exists only in newer source, not the installed `v0.0.27`.
`QueryAs[T]` is a package function, not a generic database method.
`Row.Scan(&value, ...)` reads positional columns and returns an error to check.
Sources: [Go](https://docs.terrascale.tech/getting-started/go),
[SQL HTTP](https://docs.terrascale.tech/guides/sql-http).

Pass caller cancellation through `context.Context`; use `context.WithTimeout`
for a shorter application budget. `Transaction(ctx, func(*SQLSession) error)`
commits on nil and rolls back on a callback error. Explicit `Begin`, `Commit`,
`Rollback`, `InTransaction` and `Close` exist on the session.

Use `errors.As` for `*tb.SQLError` (`Code`, `Statement`, `SQLState`, `Status`)
and `*tb.APIError` (`Status`, `Code`, `RetryAfter`, `RequestID`).
For `v0.0.27`, classify both `commit_uncertain` and `write_outcome_unknown` before
any replay decision. Its `SQLError.Is` only matches its own code; do not rely on
`errors.Is(err, tb.ErrCommitUncertain)` alone for a session SQL error. With the
existing `errors` and `tb` imports, use:

```go
func uncertainWrite(err error) bool {
    var sqlErr *tb.SQLError
    if errors.As(err, &sqlErr) {
        return sqlErr.Code == tb.ErrCommitUncertain || sqlErr.Code == tb.ErrWriteOutcomeUnknown
    }
    return errors.Is(err, tb.ErrCommitUncertain) || errors.Is(err, tb.ErrWriteOutcomeUnknown)
}
```

Inspect the SQL error first instead of traversing its absent API-error wrapper.
After either uncertain code or a lost potentially committing reply, explicitly
`Close()` and discard the session. Reconcile the expected state through a new
connection before deciding whether to repeat the write. The complete example
returns transaction errors and closes its session through `defer`; it never replays.

The released session can retain a reconnect path after a lost committing reply.
A later local `transaction_rolled_back` response or successful `Rollback` is not
proof that the earlier uncertain commit rolled back. Never clear that uncertainty
by reusing the session. Only a transaction known to have lost no committing
request may use `Rollback` to clear lost-transaction state before reuse.

Newer source makes `SQLError.Is` match `ErrCommitUncertain` for
`write_outcome_unknown` and permanently fails the session after a lost committing
reply. These guarantees do not apply to `v0.0.27`.
Do not log credentials or argument values when reporting request metadata.
Source: [safe retries](https://docs.terrascale.tech/guides/errors).
Source audit: `sdk/go/README.md`, `v1/client.go`, `database.go`, `sql_execute.go`,
`sql_session.go`, `errors.go`, `ids.go`, and `tsauth` at the revision in
[the skill](../SKILL.md). The named-argument and recovery differences were also
checked against tag `sdk/go/v0.0.27` and source commit
`6b786568d60373e2fa16836a8ab4354bbb642651`. Source-only methods require a source dependency.
