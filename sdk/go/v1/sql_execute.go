package v1

import (
	"bytes"
	"context"
	"database/sql"
	"database/sql/driver"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"net/http"
	"reflect"
	"strings"
)

const (
	MaxSQLStatements   = 256
	MaxSQLRequestBytes = 1 << 20
	maxSQLResends      = 2
)

const (
	ErrSQL                   ErrorCode = "sql_error"
	ErrSyntax                ErrorCode = "syntax_error"
	ErrUndefinedTable        ErrorCode = "undefined_table"
	ErrUndefinedColumn       ErrorCode = "undefined_column"
	ErrDuplicateObject       ErrorCode = "duplicate_object"
	ErrPrimaryKeyViolation   ErrorCode = "primary_key_violation"
	ErrUniqueViolation       ErrorCode = "unique_violation"
	ErrNotNullViolation      ErrorCode = "not_null_violation"
	ErrCheckViolation        ErrorCode = "check_violation"
	ErrForeignKeyViolation   ErrorCode = "foreign_key_violation"
	ErrConstraintViolation   ErrorCode = "constraint_violation"
	ErrSQLSandbox            ErrorCode = "sql_sandbox" // 403: the statement leaves the SQL sandbox (ATTACH, VACUUM, file functions, …)
	ErrSerializationConflict ErrorCode = "serialization_conflict"
	ErrRowConflict           ErrorCode = "row_conflict"
	ErrTooLarge              ErrorCode = "too_large"
	ErrQueryCanceled         ErrorCode = "query_canceled"
	ErrTransactionRolledBack ErrorCode = "transaction_rolled_back"
	ErrTransactionAborted    ErrorCode = "transaction_aborted"
	ErrReadOnlyTransaction   ErrorCode = "read_only_transaction"
	ErrTransactionState      ErrorCode = "transaction_state"
	ErrUnauthorized          ErrorCode = "unauthorized"
	ErrUnavailable           ErrorCode = "unavailable"
)

type Statement struct {
	SQL   string         `json:"query"`
	Args  []any          `json:"args,omitempty"`
	Named map[string]any `json:"named,omitempty"`
}

type Column struct {
	Name     string  `json:"name"`
	DeclType *string `json:"decltype"`
}

type Result struct {
	Columns         []Column `json:"columns"`
	Rows            []Row    `json:"rows"`
	RowsAffected    int64    `json:"rows_affected"`
	LastInsertRowID *int64   `json:"last_insert_rowid"`
}

type Row struct {
	columns []Column
	values  []any
}

type SQLError struct {
	Code      ErrorCode
	Message   string
	Statement int
	SQLState  string
	Status    int
	API       *APIError
}

func (e *SQLError) Error() string {
	msg := "terrabase: " + string(e.Code)
	if e.Status != 0 {
		msg = fmt.Sprintf("terrabase: %d %s", e.Status, e.Code)
	}
	if e.Message != "" {
		msg += ": " + e.Message
	}
	msg += fmt.Sprintf(" (statement %d)", e.Statement)
	if e.SQLState != "" {
		msg += " [" + e.SQLState + "]"
	}
	return msg
}

func (e *SQLError) Unwrap() error { return e.API }

// Is matches the error's code constant, also for session errors that carry no *APIError.
func (e *SQLError) Is(target error) bool { return target == e.Code }

func (e *SQLError) IsTransient() bool {
	return e.Code == ErrSerializationConflict || e.Code == ErrTenantBusy
}

type SQLProtocolError struct{}

func (*SQLProtocolError) Error() string {
	return "terrabase: invalid SQL success response; transaction outcome unknown"
}

func (*SQLProtocolError) Is(target error) bool { return target == ErrCommitUncertain }

func (d *Database) Execute(ctx context.Context, query string, args ...any) (*Result, error) {
	out, err := d.Batch(ctx, []Statement{statementOf(query, args)})
	if err != nil {
		return nil, err
	}
	return &out[0], nil
}

// statementOf binds sql.NamedArg values by name and every other value by position.
func statementOf(query string, args []any) Statement {
	st := Statement{SQL: query}
	for _, a := range args {
		if n, ok := a.(sql.NamedArg); ok {
			if st.Named == nil {
				st.Named = map[string]any{}
			}
			st.Named[n.Name] = n.Value
		} else {
			st.Args = append(st.Args, a)
		}
	}
	return st
}

func (d *Database) Query(ctx context.Context, query string, args ...any) ([]Row, error) {
	res, err := d.Execute(ctx, query, args...)
	if err != nil {
		return nil, err
	}
	return res.Rows, nil
}

// SQLQuerier runs one SQL statement and returns its rows: a *Database or a *SQLSession.
type SQLQuerier interface {
	Query(ctx context.Context, query string, args ...any) ([]Row, error)
}

func QueryAs[T any](ctx context.Context, q SQLQuerier, query string, args ...any) ([]T, error) {
	rows, err := q.Query(ctx, query, args...)
	if err != nil {
		return nil, err
	}
	out := make([]T, len(rows))
	for i, row := range rows {
		data, err := json.Marshal(row.Map())
		if err != nil {
			return nil, err
		}
		dec := json.NewDecoder(bytes.NewReader(data))
		dec.UseNumber()
		if err := dec.Decode(&out[i]); err != nil {
			return nil, fmt.Errorf("terrabase: map row %d to %T: %w", i, out[i], err)
		}
	}
	return out, nil
}

func (d *Database) Batch(ctx context.Context, statements []Statement) ([]Result, error) {
	wire, err := wireStatements(statements)
	if err != nil {
		return nil, err
	}
	body, err := json.Marshal(struct {
		Statements []Statement `json:"statements"`
	}{wire})
	if err != nil {
		return nil, fmt.Errorf("terrabase: encode SQL request: %w", err)
	}
	if len(body) > MaxSQLRequestBytes {
		return nil, fmt.Errorf("terrabase: SQL request is %d bytes, over %d", len(body), MaxSQLRequestBytes)
	}
	p, err := d.path("sql", "execute")
	if err != nil {
		return nil, err
	}
	for attempt := 0; ; attempt++ {
		out, err := d.batchOnce(ctx, p, body, len(statements))
		var sqlErr *SQLError
		if errors.As(err, &sqlErr) && sqlErr.Code == ErrSerializationConflict && attempt < maxSQLResends {
			if err := sleep(ctx, backoff(attempt)); err != nil {
				return nil, err
			}
			continue
		}
		return out, err
	}
}

// wireStatements validates statements and encodes their arguments for the wire.
func wireStatements(statements []Statement) ([]Statement, error) {
	if len(statements) == 0 || len(statements) > MaxSQLStatements {
		return nil, fmt.Errorf("terrabase: SQL batch needs 1 to %d statements, got %d", MaxSQLStatements, len(statements))
	}
	wire := make([]Statement, len(statements))
	for i, st := range statements {
		if strings.TrimSpace(st.SQL) == "" {
			return nil, fmt.Errorf("terrabase: SQL statement %d is empty", i)
		}
		w := Statement{SQL: st.SQL, Args: make([]any, len(st.Args))}
		var err error
		for j, v := range st.Args {
			if w.Args[j], err = sqlArg(v); err != nil {
				return nil, fmt.Errorf("terrabase: SQL statement %d arg %d: %w", i, j, err)
			}
		}
		if len(st.Named) > 0 {
			w.Named = make(map[string]any, len(st.Named))
		}
		for k, v := range st.Named {
			if w.Named[strings.TrimLeft(k, ":@$")], err = sqlArg(v); err != nil {
				return nil, fmt.Errorf("terrabase: SQL statement %d arg %q: %w", i, k, err)
			}
		}
		wire[i] = w
	}
	return wire, nil
}

func (d *Database) batchOnce(ctx context.Context, p []string, body []byte, expected int) ([]Result, error) {
	resp, err := d.c.do(ctx, request{method: http.MethodPost, path: p, body: body, replaySafe: true})
	if apiErr := (*APIError)(nil); errors.As(err, &apiErr) {
		return nil, sqlErrorFrom(apiErr)
	}
	if err != nil {
		return nil, err
	}
	var out struct {
		Results []Result `json:"results"`
	}
	if decodeExact(resp.body, &out) != nil || len(out.Results) != expected {
		return nil, &SQLProtocolError{}
	}
	return out.Results, nil
}

func sqlArg(v any) (any, error) {
	if valuer, ok := v.(driver.Valuer); ok {
		value, err := valuer.Value()
		if err != nil {
			return nil, err
		}
		v = value
	}
	switch v := v.(type) {
	case nil, string, json.Number, int, int8, int16, int32, int64, uint8, uint16, uint32:
		return v, nil
	case uint:
		return uintArg(uint64(v))
	case uint64:
		return uintArg(v)
	case bool:
		if v {
			return 1, nil
		}
		return 0, nil
	case float32:
		return floatArg(float64(v))
	case float64:
		return floatArg(v)
	case []byte:
		return blob{v}, nil
	}
	return nil, fmt.Errorf("unsupported type %T", v)
}

func uintArg(v uint64) (any, error) {
	if v > math.MaxInt64 {
		return nil, fmt.Errorf("integer %d exceeds 64-bit signed range", v)
	}
	return v, nil
}

func floatArg(v float64) (any, error) {
	if math.IsNaN(v) || math.IsInf(v, 0) {
		return nil, errors.New("NaN and infinity are not SQL values")
	}
	return v, nil
}

type blob struct{ bytes []byte }

func (b blob) MarshalJSON() ([]byte, error) {
	return json.Marshal(map[string]string{"base64": base64.StdEncoding.EncodeToString(b.bytes)})
}

func (r *Result) UnmarshalJSON(data []byte) error {
	var wire struct {
		Columns         []Column            `json:"columns"`
		Rows            [][]json.RawMessage `json:"rows"`
		RowsAffected    *int64              `json:"rows_affected"`
		LastInsertRowID *int64              `json:"last_insert_rowid"`
	}
	if err := json.Unmarshal(data, &wire); err != nil || wire.Columns == nil || wire.Rows == nil || wire.RowsAffected == nil {
		return &SQLProtocolError{}
	}
	rows := make([]Row, len(wire.Rows))
	for i, raw := range wire.Rows {
		if len(raw) != len(wire.Columns) {
			return &SQLProtocolError{}
		}
		values := make([]any, len(raw))
		for j, cell := range raw {
			v, err := sqlCell(cell)
			if err != nil {
				return err
			}
			values[j] = v
		}
		rows[i] = Row{columns: wire.Columns, values: values}
	}
	*r = Result{Columns: wire.Columns, Rows: rows, RowsAffected: *wire.RowsAffected, LastInsertRowID: wire.LastInsertRowID}
	return nil
}

func sqlCell(cell json.RawMessage) (any, error) {
	var v any
	if decodeExact(cell, &v) != nil {
		return nil, &SQLProtocolError{}
	}
	switch v := v.(type) {
	case nil, string:
		return v, nil
	case json.Number:
		if i, err := v.Int64(); err == nil {
			return i, nil
		}
		if f, err := v.Float64(); err == nil && !math.IsInf(f, 0) {
			return f, nil
		}
	case map[string]any:
		if s, ok := v["base64"].(string); ok && len(v) == 1 {
			if b, err := base64.StdEncoding.DecodeString(s); err == nil {
				return b, nil
			}
		}
	}
	return nil, &SQLProtocolError{}
}

func (r Row) Columns() []Column { return r.columns }

func (r Row) Values() []any { return r.values }

func (r Row) MarshalJSON() ([]byte, error) {
	out := make([]any, len(r.values))
	for i, v := range r.values {
		if b, ok := v.([]byte); ok {
			out[i] = blob{b}
		} else {
			out[i] = v
		}
	}
	return json.Marshal(out)
}

func (r Row) Map() map[string]any {
	m := make(map[string]any, len(r.values))
	for i, c := range r.columns {
		if _, seen := m[c.Name]; !seen {
			m[c.Name] = r.values[i]
		}
	}
	return m
}

func (r Row) Value(column string) (any, error) {
	for i, c := range r.columns {
		if c.Name == column {
			return r.values[i], nil
		}
	}
	return nil, fmt.Errorf("terrabase: no column %q", column)
}

func (r Row) IsNull(column string) bool {
	v, err := r.Value(column)
	return err == nil && v == nil
}

func (r Row) Int64(column string) (int64, error) { return rowValue[int64](r, column) }

func (r Row) String(column string) (string, error) { return rowValue[string](r, column) }

func (r Row) Bytes(column string) ([]byte, error) { return rowValue[[]byte](r, column) }

func (r Row) Bool(column string) (bool, error) {
	v, err := r.Int64(column)
	return v != 0, err
}

func (r Row) Float64(column string) (float64, error) {
	if v, err := r.Value(column); err != nil {
		return 0, err
	} else if i, ok := v.(int64); ok {
		return float64(i), nil
	}
	return rowValue[float64](r, column)
}

func rowValue[T any](r Row, column string) (T, error) {
	var zero T
	v, err := r.Value(column)
	if err != nil {
		return zero, err
	}
	t, ok := v.(T)
	if !ok {
		return zero, fmt.Errorf("terrabase: column %q holds %T, not %T", column, v, zero)
	}
	return t, nil
}

func (r Row) Scan(dest ...any) error {
	if len(dest) != len(r.values) {
		return fmt.Errorf("terrabase: Scan got %d destinations for %d columns", len(dest), len(r.values))
	}
	for i, d := range dest {
		if err := scanValue(d, r.values[i]); err != nil {
			return fmt.Errorf("terrabase: Scan column %d (%s): %w", i, r.columns[i].Name, err)
		}
	}
	return nil
}

func scanValue(dest, v any) error {
	if s, ok := dest.(sql.Scanner); ok {
		return s.Scan(v)
	}
	switch d := dest.(type) {
	case *any:
		*d = v
		return nil
	case *bool:
		if i, ok := v.(int64); ok {
			*d = i != 0
			return nil
		}
	case *int:
		if i, ok := v.(int64); ok && int64(int(i)) == i {
			*d = int(i)
			return nil
		}
	case *float64:
		switch n := v.(type) {
		case float64:
			*d = n
			return nil
		case int64:
			*d = float64(n)
			return nil
		}
	case *[]byte:
		if b, ok := v.([]byte); ok || v == nil {
			*d = bytes.Clone(b)
			return nil
		}
	}
	rv := reflect.ValueOf(dest)
	if rv.Kind() == reflect.Pointer && !rv.IsNil() && v != nil && reflect.TypeOf(v) == rv.Elem().Type() {
		rv.Elem().Set(reflect.ValueOf(v))
		return nil
	}
	return fmt.Errorf("cannot store %T in %T", v, dest)
}

func sqlErrorFrom(apiErr *APIError) error {
	var env struct {
		Error struct {
			Statement *int   `json:"statement"`
			SQLState  string `json:"sqlstate"`
		} `json:"error"`
	}
	if json.Unmarshal(apiErr.Body, &env) != nil || env.Error.Statement == nil {
		return apiErr
	}
	return &SQLError{Code: apiErr.Code, Message: apiErr.Message, Statement: *env.Error.Statement, SQLState: env.Error.SQLState, Status: apiErr.Status, API: apiErr}
}
