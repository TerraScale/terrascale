package v1

import (
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"io"
)

// OpenDB returns a *sql.DB backed by db, for use with database/sql and libraries built on it.
func OpenDB(db *Database) *sql.DB {
	return sql.OpenDB(dbConnector{db: db})
}

type dbConnector struct {
	db *Database
}

func (c dbConnector) Connect(context.Context) (driver.Conn, error) {
	return dbConn{db: c.db}, nil
}

func (c dbConnector) Driver() driver.Driver {
	return dbDriver{}
}

type dbDriver struct{}

func (dbDriver) Open(string) (driver.Conn, error) {
	return nil, errors.New("terrascale: no DSN form; build a *Database and pass it to OpenDB")
}

type dbConn struct {
	db *Database
}

func (c dbConn) Prepare(query string) (driver.Stmt, error) {
	return dbStmt{conn: c, query: query}, nil
}

func (c dbConn) Close() error {
	return nil
}

func (c dbConn) Begin() (driver.Tx, error) {
	return nil, errors.New("terrascale: transactions arrive in the next SDK release")
}

func (c dbConn) ExecContext(ctx context.Context, query string, args []driver.NamedValue) (driver.Result, error) {
	res, err := c.db.Execute(ctx, query, namedValues(args)...)
	if err != nil {
		return nil, err
	}
	return execResult{res: res}, nil
}

func (c dbConn) QueryContext(ctx context.Context, query string, args []driver.NamedValue) (driver.Rows, error) {
	res, err := c.db.Execute(ctx, query, namedValues(args)...)
	if err != nil {
		return nil, err
	}
	names := make([]string, len(res.Columns))
	for i, col := range res.Columns {
		names[i] = col.Name
	}
	return &execRows{columns: names, rows: res.Rows}, nil
}

func namedValues(args []driver.NamedValue) []any {
	if len(args) == 0 {
		return nil
	}
	out := make([]any, len(args))
	for i, arg := range args {
		if arg.Name != "" {
			out[i] = sql.Named(arg.Name, arg.Value)
		} else {
			out[i] = arg.Value
		}
	}
	return out
}

type execResult struct {
	res *Result
}

func (r execResult) LastInsertId() (int64, error) {
	if r.res.LastInsertRowID != nil {
		return *r.res.LastInsertRowID, nil
	}
	return 0, nil
}

func (r execResult) RowsAffected() (int64, error) {
	return r.res.RowsAffected, nil
}

type execRows struct {
	columns []string
	rows    []Row
	next    int
}

func (r *execRows) Columns() []string {
	return r.columns
}

func (r *execRows) Close() error {
	r.rows = nil
	r.next = 0
	return nil
}

func (r *execRows) Next(dest []driver.Value) error {
	if r.next >= len(r.rows) {
		return io.EOF
	}
	values := r.rows[r.next].Values()
	if len(dest) != len(values) {
		return errors.New("terrabase: column count mismatch")
	}
	for i, v := range values {
		dest[i] = v
	}
	r.next++
	return nil
}

type dbStmt struct {
	conn  dbConn
	query string
}

func (s dbStmt) Close() error {
	return nil
}

func (s dbStmt) NumInput() int {
	return -1
}

func (s dbStmt) ExecContext(ctx context.Context, args []driver.NamedValue) (driver.Result, error) {
	return s.conn.ExecContext(ctx, s.query, args)
}

func (s dbStmt) QueryContext(ctx context.Context, args []driver.NamedValue) (driver.Rows, error) {
	return s.conn.QueryContext(ctx, s.query, args)
}

func (s dbStmt) Exec(args []driver.Value) (driver.Result, error) {
	return s.conn.ExecContext(context.Background(), s.query, namedArgs(args))
}

func (s dbStmt) Query(args []driver.Value) (driver.Rows, error) {
	return s.conn.QueryContext(context.Background(), s.query, namedArgs(args))
}

func namedArgs(args []driver.Value) []driver.NamedValue {
	out := make([]driver.NamedValue, len(args))
	for i, arg := range args {
		out[i] = driver.NamedValue{Ordinal: i + 1, Value: arg}
	}
	return out
}
