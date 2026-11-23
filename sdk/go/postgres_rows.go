package sdk

import (
	"context"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
)

type PostgresRows struct {
	client   *PostgresClient
	ctx      context.Context
	rows     pgx.Rows
	columns  []PostgresColumn
	current  []PostgresValue
	rawSize  int
	err      error
	tag      pgconn.CommandTag
	released bool
	finish   func()
	onError  func(error)
}

func (r *PostgresRows) Next() bool {
	if r.released || r.err != nil {
		return false
	}
	if !r.rows.Next() {
		r.complete()
		return false
	}
	raw := r.rows.RawValues()
	if len(raw) != len(r.columns) {
		r.fail(&PostgresError{Kind: "decode"})
		return false
	}
	r.rawSize = 0
	for _, value := range raw {
		r.rawSize += len(value)
	}
	row := make([]PostgresValue, len(r.columns))
	for index, field := range r.rows.FieldDescriptions() {
		value, err := postgresDecode(r.client.driver.TypeMap(), field, raw[index])
		if err != nil {
			r.fail(&PostgresError{Kind: "decode"})
			return false
		}
		row[index] = PostgresValue{oid: field.DataTypeOID, value: value}
	}
	r.current = row
	return true
}

func (r *PostgresRows) Values() []PostgresValue {
	return r.current
}

func (r *PostgresRows) Err() error {
	return r.err
}

func (r *PostgresRows) CommandTag() string {
	return r.tag.String()
}

func (r *PostgresRows) Close() error {
	if !r.released && r.err == nil {
		if err := r.rows.Err(); err != nil {
			r.fail(postgresFailure(err))
			return r.err
		}
		r.tag = r.rows.CommandTag()
	}
	r.releaseOnce()
	return r.err
}

func (r *PostgresRows) complete() {
	if err := r.rows.Err(); err != nil {
		r.fail(postgresFailure(err))
		return
	}
	r.tag = r.rows.CommandTag()
	r.releaseOnce()
}

func (r *PostgresRows) fail(err error) {
	if ctxErr := r.ctx.Err(); ctxErr != nil {
		err = postgresFailure(ctxErr)
	}
	r.abort(err)
}

func (r *PostgresRows) abort(err error) {
	r.client.abort(r.ctx)
	r.err = err
	if r.onError != nil {
		r.onError(err)
	}
	r.releaseOnce()
}

func (r *PostgresRows) releaseOnce() {
	if r.released {
		return
	}
	r.released = true
	r.rows.Close()
	if err := r.client.observe(); err != nil && r.err == nil {
		r.err = err
	}
	if r.finish != nil {
		r.finish()
	}
}

func (client *PostgresClient) QueryRows(ctx context.Context, query PostgresQuery) (*PostgresRows, error) {
	if err := client.acquire(ctx); err != nil {
		return nil, err
	}
	if err := client.sync(ctx); err != nil {
		<-client.gate
		return nil, err
	}
	rows, columns, err := client.openRows(ctx, query)
	if err != nil {
		<-client.gate
		return nil, err
	}
	return &PostgresRows{client: client, ctx: ctx, rows: rows, columns: columns, finish: func() { <-client.gate }}, nil
}
