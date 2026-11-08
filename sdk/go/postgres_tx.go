package sdk

import (
	"context"
	"errors"
	"sync"
)

var (
	// ErrTxDone is returned by every PostgresTx method after Commit or Rollback.
	ErrTxDone = errors.New("terrascale PostgreSQL transaction already completed")
	// ErrTxBusy is returned when two goroutines use one PostgresTx at the same time.
	ErrTxBusy = errors.New("terrascale PostgreSQL transaction already in use")
)

// PostgresTx owns one transaction on its client's single connection.
type PostgresTx struct {
	client *PostgresClient
	mu     sync.Mutex
	inUse  bool
	done   bool
	failed error
}

// Begin holds the client's connection exclusively until Commit or Rollback.
func (client *PostgresClient) Begin(ctx context.Context) (*PostgresTx, error) {
	if err := client.acquire(ctx); err != nil {
		return nil, err
	}
	if client.driver == nil || client.driver.IsClosed() {
		<-client.gate
		return nil, &PostgresError{Kind: "connection"}
	}
	if _, err := client.run(ctx, PostgresQuery{Statement: "BEGIN"}); err != nil {
		<-client.gate
		return nil, postgresFailure(err)
	}
	return &PostgresTx{client: client}, nil
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

// Query sends the SQL and typed values separately through PostgreSQL binding.
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

// QueryRows streams the statement's rows inside the transaction without buffering.
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

// Exec runs one statement inside the transaction and returns its command tag.
func (tx *PostgresTx) Exec(ctx context.Context, query PostgresQuery) (PostgresResult, error) {
	return tx.Query(ctx, query)
}

// QueryRow returns the first row of the statement's result, or nil when none.
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

// Commit finalizes the transaction; after a failed statement it rolls back and returns that error.
func (tx *PostgresTx) Commit(ctx context.Context) error {
	if err := tx.enter(false); err != nil {
		return err
	}
	if tx.failed != nil {
		tx.discard(ctx)
		tx.finish()
		return tx.failed
	}
	if _, err := tx.client.run(ctx, PostgresQuery{Statement: "COMMIT"}); err != nil {
		tx.client.abort(ctx)
		tx.finish()
		return postgresFailure(err)
	}
	tx.finish()
	return nil
}

// Rollback discards the transaction and returns the connection to the client.
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
