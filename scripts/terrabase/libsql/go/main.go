// Proof: go-libsql against TerraBase Hrana over HTTP. See ../README.md.
package main

import (
	"context"
	"crypto/rand"
	"database/sql"
	"errors"
	"fmt"
	"net/url"
	"os"
	"strings"

	_ "github.com/tursodatabase/go-libsql"
)

func main() {
	if err := run(context.Background()); err != nil {
		fmt.Println("error:", err)
		fmt.Println("FAIL")
		os.Exit(1)
	}
	fmt.Println("PASS")
}

func run(ctx context.Context) (err error) {
	rawURL, tokenFile := os.Getenv("LIBSQL_URL"), os.Getenv("LIBSQL_TOKEN_FILE")
	if rawURL == "" || tokenFile == "" {
		return errors.New("set LIBSQL_URL and LIBSQL_TOKEN_FILE")
	}
	token, err := os.ReadFile(tokenFile)
	if err != nil {
		return fmt.Errorf("read token file: %w", err)
	}
	// go-libsql takes the token only as the authToken query parameter; the DSN stays in memory.
	u, err := url.Parse(rawURL)
	if err != nil {
		return err
	}
	u.RawQuery = url.Values{"authToken": {strings.TrimSpace(string(token))}}.Encode()
	db, err := sql.Open("libsql", u.String())
	if err != nil {
		return err
	}
	defer db.Close()

	t := "proof_go_" + strings.ToLower(rand.Text()[:8])
	owners, items := t+"_owners", t+"_items"
	defer func() {
		_, e1 := db.ExecContext(ctx, "DROP TABLE IF EXISTS "+items)
		_, e2 := db.ExecContext(ctx, "DROP TABLE IF EXISTS "+owners)
		if e := errors.Join(e1, e2); e != nil {
			err = errors.Join(err, fmt.Errorf("drop: %w", e))
		} else {
			fmt.Println("drop tables")
		}
	}()

	fmt.Printf("create tables %s, %s\n", owners, items)
	if _, err := db.ExecContext(ctx, "CREATE TABLE "+owners+" (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"); err != nil {
		return err
	}
	if _, err := db.ExecContext(ctx, "CREATE TABLE "+items+" (sku TEXT PRIMARY KEY, owner_id INTEGER NOT NULL, qty INTEGER)"); err != nil {
		return err
	}

	res, err := db.ExecContext(ctx, "INSERT INTO "+owners+" (id, name) VALUES (?, ?)", 1, "ann")
	if err != nil {
		return err
	}
	if _, err := db.ExecContext(ctx, "INSERT INTO "+owners+" (id, name) VALUES (:id, :name)", sql.Named("id", 2), sql.Named("name", "bob")); err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	fmt.Printf("insert with parameters: rowsAffected=%d\n", n)
	if n != 1 {
		return errors.New("insert should affect 1 row")
	}

	// database/sql has no batch call, and go-libsql sends only the first statement of a
	// multi-statement Exec, so the Go batch is one transaction of parameterized inserts.
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	for _, it := range []struct {
		sku        string
		owner, qty int
	}{{"a-1", 1, 3}, {"a-2", 1, 5}, {"b-1", 2, 7}} {
		if _, err := tx.ExecContext(ctx, "INSERT INTO "+items+" (sku, owner_id, qty) VALUES (?, ?, ?)", it.sku, it.owner, it.qty); err != nil {
			_ = tx.Rollback()
			return fmt.Errorf("batch: %w", err)
		}
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("batch: %w", err)
	}
	fmt.Println("batch (one transaction): 3 statements")

	rows, err := db.QueryContext(ctx, "SELECT o.name, i.sku, i.qty FROM "+items+" i JOIN "+owners+" o ON o.id = i.owner_id ORDER BY i.sku")
	if err != nil {
		return err
	}
	var got []string
	for rows.Next() {
		var name, sku string
		var qty int64
		if err := rows.Scan(&name, &sku, &qty); err != nil {
			return err
		}
		got = append(got, fmt.Sprintf("%s/%s/%d", name, sku, qty))
	}
	if err := errors.Join(rows.Err(), rows.Close()); err != nil {
		return err
	}
	fmt.Println("select with join:", strings.Join(got, ", "))
	if len(got) != 3 || !strings.HasPrefix(got[2], "bob/") {
		return errors.New("join should return 3 rows, last owned by bob")
	}

	count := func() (c int) {
		_ = db.QueryRowContext(ctx, "SELECT count(*) FROM "+items).Scan(&c)
		return c
	}
	insertInTx := func(sku string, commit bool) error {
		tx, err := db.BeginTx(ctx, nil)
		if err != nil {
			return err
		}
		defer tx.Rollback()
		if _, err := tx.ExecContext(ctx, "INSERT INTO "+items+" (sku, owner_id, qty) VALUES (?, ?, ?)", sku, 2, 1); err != nil {
			return err
		}
		var seen int
		if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM "+items+" WHERE sku = ?", sku).Scan(&seen); err != nil || seen != 1 {
			return errors.Join(err, errors.New("transaction should see its own insert"))
		}
		if commit {
			return tx.Commit()
		}
		return tx.Rollback()
	}

	if err := insertInTx("tx-1", true); err != nil {
		return fmt.Errorf("commit transaction: %w", err)
	}
	fmt.Printf("transaction commit: count=%d\n", count())
	if count() != 4 {
		return errors.New("committed row should be visible")
	}
	if err := insertInTx("tx-2", false); err != nil {
		return fmt.Errorf("rollback transaction: %w", err)
	}
	fmt.Printf("transaction rollback: count=%d\n", count())
	if count() != 4 {
		return errors.New("rolled back row should not be visible")
	}
	return nil
}
