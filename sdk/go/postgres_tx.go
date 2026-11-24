package sdk

import (
	"context"
	"errors"
	"sync"

	"terrascale.tech/sdk/internal/causal"
)

var (
	ErrTxDone = errors.New("terrascale PostgreSQL transaction already completed")
	ErrTxBusy = errors.New("terrascale PostgreSQL transaction already in use")
)

type PostgresTx struct {
	client *PostgresClient
	mu     sync.Mutex
	inUse  bool
	done   bool
	failed error
}

func (client *PostgresClient) Begin(ctx context.Context) (*PostgresTx, error) {
	mode, err := causal.ConsistencyFrom(ctx)
	if err != nil {
		return nil, &PostgresError{Kind: "query", cause: err}
	}
	if err := client.acquire(ctx); err != nil {
		return nil, err
	}
	if client.driver == nil || client.driver.IsClosed() {
		<-client.gate
		return nil, &PostgresError{Kind: "connection"}
	}
	if err := client.sync(ctx); err != nil {
		<-client.gate
		return nil, err
	}
	statement := "BEGIN"
	switch mode {
	case ConsistencyStrong:
		statement = "BEGIN ISOLATION LEVEL SERIALIZABLE"
	case ConsistencyFast:
		statement = "BEGIN ISOLATION LEVEL READ COMMITTED"
	}
	if _, err := client.run(ctx, PostgresQuery{Statement: statement}); err != nil {
		<-client.gate
		return nil, err
	}
	return &PostgresTx{client: client}, nil
}

func (client *PostgresClient) BeginStrong(ctx context.Context) (*PostgresTx, error) {
	return client.Begin(WithConsistency(ctx, ConsistencyStrong))
}

func (tx *PostgresTx) enter(statement bool) error {
	tx.mu.Lock()
	defer tx.mu.Unlock()
	if tx.inUse {
		return ErrTxBusy
	}
	if tx.done || statement && tx.failed != nil {
		return ErrTxDone
	}
	tx.inUse = true
	return nil
}

func (tx *PostgresTx) exit() {
	tx.mu.Lock()
	tx.inUse = false
	tx.mu.Unlock()
}

func (tx *PostgresTx) finish() {
	tx.mu.Lock()
	tx.done = true
	tx.inUse = false
	tx.mu.Unlock()
	<-tx.client.gate
}

func (tx *PostgresTx) discard(ctx context.Context) {
	if _, err := tx.client.run(ctx, PostgresQuery{Statement: "ROLLBACK"}); err != nil {
		tx.client.abort(ctx)
	}
}

func (tx *PostgresTx) failWith(err error) {
	tx.mu.Lock()
	if tx.failed == nil {
		tx.failed = err
	}
	tx.mu.Unlock()
}

func (tx *PostgresTx) Query(ctx context.Context, query PostgresQuery) (PostgresResult, error) {
	if err := tx.enter(true); err != nil {
		return PostgresResult{}, err
	}
	defer tx.exit()
	result, err := tx.client.run(ctx, query)
	if err != nil {
		tx.failWith(err)
		return PostgresResult{}, err
	}
	return result, nil
}

func (tx *PostgresTx) QueryRows(ctx context.Context, query PostgresQuery) (*PostgresRows, error) {
	if err := tx.enter(true); err != nil {
		return nil, err
	}
	rows, columns, err := tx.client.openRows(ctx, query)
	if err != nil {
		tx.failWith(err)
		tx.exit()
		return nil, err
	}
	return &PostgresRows{
		client: tx.client, ctx: ctx, rows: rows, columns: columns,
		finish: tx.exit, onError: tx.failWith,
	}, nil
}

func (tx *PostgresTx) Exec(ctx context.Context, query PostgresQuery) (PostgresResult, error) {
	return tx.Query(ctx, query)
}

func (tx *PostgresTx) QueryRow(ctx context.Context, query PostgresQuery) ([]PostgresValue, error) {
	result, err := tx.Query(ctx, query)
	if err != nil {
		return nil, err
	}
	if len(result.Rows) == 0 {
		return nil, nil
	}
	return result.Rows[0], nil
}

func (tx *PostgresTx) Commit(ctx context.Context) error {
	if err := tx.enter(false); err != nil {
		return err
	}
	defer tx.finish()
	if tx.failed != nil {
		tx.discard(ctx)
		return tx.failed
	}
	if tx.client.driver.IsClosed() {
		return &PostgresError{Kind: "connection"}
	}
	if err := ctx.Err(); err != nil {
		tx.client.abort(ctx)
		return postgresFailure(err)
	}
	if _, err := tx.client.driver.Exec(ctx, "COMMIT"); err != nil {
		tx.client.abort(ctx)
		return postgresCommitFailure(err)
	}
	return tx.client.observe()
}

func (tx *PostgresTx) Rollback(ctx context.Context) error {
	if err := tx.enter(false); err != nil {
		return err
	}
	if tx.failed != nil {
		tx.discard(ctx)
		tx.finish()
		return nil
	}
	if _, err := tx.client.run(ctx, PostgresQuery{Statement: "ROLLBACK"}); err != nil {
		tx.client.abort(ctx)
		tx.finish()
		return postgresFailure(err)
	}
	tx.finish()
	return nil
}
