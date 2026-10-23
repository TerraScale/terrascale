// Smoke test: pgx v5 against TerraBase over the Postgres wire. See ../README.md.
package main

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"os"
	"strings"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
)

type step struct {
	name string
	run  func(context.Context) error
}

func main() {
	if err := run(context.Background()); err != nil {
		fmt.Println("FAIL")
		os.Exit(1)
	}
	fmt.Println("PASS")
}

func run(ctx context.Context) (err error) {
	url := os.Getenv("TERRABASE_PG_URL")
	if url == "" {
		fmt.Println("set TERRABASE_PG_URL")
		os.Exit(2)
	}
	cfg, err := pgx.ParseConfig(url)
	if err != nil {
		fmt.Println("FAIL parse TERRABASE_PG_URL") // the parse error can echo the URL, so it is not printed
		return err
	}
	fmt.Printf("target %s:%d db=%s\n", cfg.Host, cfg.Port, cfg.Database)
	conn, err := pgx.ConnectConfig(ctx, cfg)
	if err != nil {
		fmt.Println("FAIL connect:", err)
		return err
	}
	defer conn.Close(ctx)

	t := "smoke_go_" + strings.ToLower(rand.Text()[:8])
	owners, items := t+"_owners", t+"_items"
	insert := "INSERT INTO " + items + " (sku, owner_id, qty) VALUES ($1, $2, $3)"
	defer func() {
		_, e1 := conn.Exec(ctx, "DROP TABLE IF EXISTS "+items)
		_, e2 := conn.Exec(ctx, "DROP TABLE IF EXISTS "+owners)
		if e := errors.Join(e1, e2); e != nil {
			fmt.Println("FAIL drop tables:", e)
			err = errors.Join(err, e)
		} else {
			fmt.Println("PASS drop tables")
		}
	}()

	count := func(ctx context.Context) (n int64, err error) {
		err = conn.QueryRow(ctx, "SELECT count(*) FROM "+items).Scan(&n)
		return n, err
	}
	affected := func(tag pgconn.CommandTag, err error, want int64) error {
		if err != nil {
			return err
		}
		if got := tag.RowsAffected(); got != want {
			return fmt.Errorf("rows affected=%d, want %d", got, want)
		}
		return nil
	}
	wantCount := func(ctx context.Context, want int64) error {
		n, err := count(ctx)
		if err == nil && n != want {
			err = fmt.Errorf("count=%d, want %d", n, want)
		}
		return err
	}

	steps := []step{
		{"create tables (integer and text primary keys)", func(ctx context.Context) error {
			if _, err := conn.Exec(ctx, "CREATE TABLE "+owners+" (id integer PRIMARY KEY, name text NOT NULL)"); err != nil {
				return err
			}
			_, err := conn.Exec(ctx, "CREATE TABLE "+items+" (sku text PRIMARY KEY, owner_id integer NOT NULL, qty integer)")
			return err
		}},
		{"insert (simple protocol, literals)", func(ctx context.Context) error {
			tag, err := conn.Exec(ctx, "INSERT INTO "+owners+" (id, name) VALUES (1, 'ann'), (2, 'bob')", pgx.QueryExecModeSimpleProtocol)
			if err := affected(tag, err, 2); err != nil {
				return err
			}
			_, err = conn.Exec(ctx, "INSERT INTO "+items+" (sku, owner_id, qty) VALUES ('a-1', 1, 3)", pgx.QueryExecModeSimpleProtocol)
			return err
		}},
		{"insert with parameters (extended protocol, unnamed statement)", func(ctx context.Context) error {
			tag, err := conn.Exec(ctx, insert, pgx.QueryExecModeExec, "a-2", 1, 5)
			return affected(tag, err, 1)
		}},
		{"insert with a named prepared statement, reused", func(ctx context.Context) error {
			if _, err := conn.Prepare(ctx, "ins", insert); err != nil {
				return err
			}
			for _, r := range [][]any{{"b-1", 2, 7}, {"b-2", 2, 9}} {
				tag, err := conn.Exec(ctx, "ins", r...)
				if err := affected(tag, err, 1); err != nil {
					return err
				}
			}
			return wantCount(ctx, 4)
		}},
		{"update with affected-row count", func(ctx context.Context) error {
			tag, err := conn.Exec(ctx, "UPDATE "+items+" SET qty = qty + 1 WHERE owner_id = $1", 2)
			return affected(tag, err, 2)
		}},
		{"delete with affected-row count", func(ctx context.Context) error {
			tag, err := conn.Exec(ctx, "DELETE FROM "+items+" WHERE sku = $1", "b-2")
			if err := affected(tag, err, 1); err != nil {
				return err
			}
			return wantCount(ctx, 3)
		}},
		{"select with a join and a parameter", func(ctx context.Context) error {
			rows, _ := conn.Query(ctx, "SELECT o.name, i.sku, i.qty FROM "+items+" i JOIN "+owners+" o ON o.id = i.owner_id WHERE o.name = $1 ORDER BY i.sku", "ann")
			var got []string
			var name, sku string
			var qty int32
			if _, err := pgx.ForEachRow(rows, []any{&name, &sku, &qty}, func() error {
				got = append(got, fmt.Sprintf("%s/%s/%d", name, sku, qty))
				return nil
			}); err != nil {
				return err
			}
			if s := strings.Join(got, ","); s != "ann/a-1/3,ann/a-2/5" {
				return fmt.Errorf("rows=%s", s)
			}
			return nil
		}},
		{"transaction commit persists", func(ctx context.Context) error {
			tx, err := conn.Begin(ctx)
			if err != nil {
				return err
			}
			defer tx.Rollback(ctx) //nolint:errcheck // no-op after Commit
			if _, err := tx.Exec(ctx, insert, "tx-1", 2, 1); err != nil {
				return err
			}
			if err := tx.Commit(ctx); err != nil {
				return err
			}
			return wantCount(ctx, 4)
		}},
		{"transaction rollback discards", func(ctx context.Context) error {
			tx, err := conn.Begin(ctx)
			if err != nil {
				return err
			}
			if _, err := tx.Exec(ctx, insert, "tx-2", 2, 1); err != nil {
				tx.Rollback(ctx) //nolint:errcheck
				return err
			}
			if err := tx.Rollback(ctx); err != nil {
				return err
			}
			return wantCount(ctx, 4)
		}},
		{"unique violation is SQLSTATE 23505", func(ctx context.Context) error {
			_, err := conn.Exec(ctx, insert, "a-1", 1, 1)
			var pgErr *pgconn.PgError
			if !errors.As(err, &pgErr) {
				return fmt.Errorf("want a PgError, got %v", err)
			}
			if pgErr.Code != "23505" {
				return fmt.Errorf("sqlstate=%s", pgErr.Code)
			}
			return nil
		}},
		{"prepared select reused", func(ctx context.Context) error {
			if _, err := conn.Prepare(ctx, "qty", "SELECT qty FROM "+items+" WHERE sku = $1"); err != nil {
				return err
			}
			for sku, want := range map[string]int32{"a-1": 3, "b-1": 8} {
				var qty int32
				if err := conn.QueryRow(ctx, "qty", sku).Scan(&qty); err != nil {
					return err
				}
				if qty != want {
					return fmt.Errorf("%s qty=%d, want %d", sku, qty, want)
				}
			}
			return nil
		}},
	}

	for _, s := range steps {
		if err := s.run(ctx); err != nil {
			fmt.Printf("FAIL %s: %v\n", s.name, err)
			return err
		}
		fmt.Printf("PASS %s\n", s.name)
	}
	return nil
}
