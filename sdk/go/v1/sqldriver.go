package v1

import (
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"fmt"
	"io"
	"strings"
)

// OpenDB returns a *sql.DB backed by db, for use with database/sql and libraries built on it.
func OpenDB(db *Database) *sql.DB {
	return sql.OpenDB(dbConnector{db: db})
}

type dbConnector struct {
	db *Database
}

func (c dbConnector) Connect(context.Context) (driver.Conn, error) {
	return &dbConn{db: c.db}, nil
}

func (c dbConnector) Driver() driver.Driver {
	return dbDriver{}
}

type dbDriver struct{}

func (dbDriver) Open(string) (driver.Conn, error) {
	return nil, errors.New("terrascale: no DSN form; build a *Database and pass it to OpenDB")
}

type dbConn struct {
	db      *Database
	session *SQLSession
}

func (c *dbConn) Prepare(query string) (driver.Stmt, error) {
	return dbStmt{conn: c, query: query}, nil
}

func (c *dbConn) Close() error {
	return c.endSession()
}

func (c *dbConn) Begin() (driver.Tx, error) {
	return c.BeginTx(context.Background(), driver.TxOptions{})
}

func (c *dbConn) BeginTx(ctx context.Context, opts driver.TxOptions) (driver.Tx, error) {
	if opts.Isolation != driver.IsolationLevel(sql.LevelDefault) && opts.Isolation != driver.IsolationLevel(sql.LevelSerializable) {
		return nil, fmt.Errorf("terrascale: isolation level %d is not supported; use LevelDefault or LevelSerializable", opts.Isolation)
	}
	if c.session != nil {
		return nil, errors.New("terrascale: transaction already open on this connection")
	}
	s, err := c.db.Connect(ctx)
	if err != nil {
		return nil, err
	}
	if err := s.Begin(ctx); err != nil {
		_ = s.Close()
		return nil, err
	}
	c.session = s
	return dbTx{conn: c}, nil
}

func (c *dbConn) endSession() error {
	s := c.session
	c.session = nil
	if s == nil {
		return nil
	}
	return s.Close()
}

func (c *dbConn) ResetSession(context.Context) error {
	if c.session == nil {
		return nil
	}
	_ = c.endSession()
	return driver.ErrBadConn
}

func (c *dbConn) IsValid() bool {
	return c.session == nil
}

func (c *dbConn) ExecContext(ctx context.Context, query string, args []driver.NamedValue) (driver.Result, error) {
	res, err := c.execute(ctx, query, args)
	if err != nil {
		return nil, err
	}
	return execResult{res: res}, nil
}

func (c *dbConn) QueryContext(ctx context.Context, query string, args []driver.NamedValue) (driver.Rows, error) {
	res, err := c.execute(ctx, query, args)
	if err != nil {
		return nil, err
	}
	names := make([]string, len(res.Columns))
	declTypes := make([]string, len(res.Columns))
	for i, col := range res.Columns {
		names[i] = col.Name
		if col.DeclType != nil {
			declTypes[i] = *col.DeclType
		}
	}
	return &execRows{columns: names, declTypes: declTypes, rows: res.Rows}, nil
}

func (c *dbConn) execute(ctx context.Context, query string, args []driver.NamedValue) (*Result, error) {
	if c.session != nil {
		return c.session.Execute(ctx, query, namedValues(args)...)
	}
	return c.db.Execute(ctx, query, namedValues(args)...)
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

type dbTx struct {
	conn *dbConn
}

func (t dbTx) Commit() error {
	s := t.conn.session
	t.conn.session = nil
	if s == nil {
		return errors.New("terrascale: transaction already finished")
	}
	err := s.Commit(context.Background())
	_ = s.Close()
	return err
}

func (t dbTx) Rollback() error {
	s := t.conn.session
	t.conn.session = nil
	if s == nil {
		return errors.New("terrascale: transaction already finished")
	}
	err := s.Rollback(context.Background())
	_ = s.Close()
	return err
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
	columns   []string
	declTypes []string
	rows      []Row
	next      int
}

func (r *execRows) Columns() []string {
	return r.columns
}

func (r *execRows) ColumnTypeDatabaseTypeName(index int) string {
	if index < 0 || index >= len(r.declTypes) {
		return ""
	}
	return strings.ToUpper(r.declTypes[index])
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
		dest[i] = normalizeRowValue(v, r.declTypes[i])
	}
	r.next++
	return nil
}

type dbStmt struct {
	conn  *dbConn
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
