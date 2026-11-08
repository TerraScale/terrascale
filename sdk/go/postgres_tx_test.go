package sdk

import (
	"errors"
	"testing"
)

func TestPostgresTransactionLifecycle(t *testing.T) {
	client := postgresFixtureClient(t, postgresFixture{tag: "INSERT 0 1"})
	ctx := t.Context()
	tx, err := client.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	result, err := tx.Exec(ctx, PostgresQuery{Statement: "INSERT INTO items (id) VALUES (01ARZ3NDEKTSV4RRFFQ69G5FAEV)"})
	if err != nil || result.CommandTag != "INSERT 0 1" || result.AffectedRows != 1 {
		t.Fatalf("exec: %v %v", result, err)
	}
	if err := tx.Commit(ctx); err != nil {
		t.Fatal(err)
	}
	if _, err := tx.Exec(ctx, PostgresQuery{Statement: "SELECT 1"}); !errors.Is(err, ErrTxDone) {
		t.Fatalf("use after commit: %v", err)
	}

	rolled, err := client.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := rolled.Exec(ctx, PostgresQuery{Statement: "INSERT INTO items (id) VALUES (01ARZ3NDEKTSV4RRFFQ69G5FAEV)"}); err != nil {
		t.Fatal(err)
	}
	if err := rolled.Rollback(ctx); err != nil {
		t.Fatal(err)
	}
	if _, err := rolled.Query(ctx, PostgresQuery{Statement: "SELECT 1"}); !errors.Is(err, ErrTxDone) {
		t.Fatalf("use after rollback: %v", err)
	}
	if err := rolled.Commit(ctx); !errors.Is(err, ErrTxDone) {
		t.Fatalf("commit after rollback: %v", err)
	}

	if _, err := client.Query(ctx, PostgresQuery{Statement: "SELECT 1"}); err != nil {
		t.Fatalf("client gate not released: %v", err)
	}
}
