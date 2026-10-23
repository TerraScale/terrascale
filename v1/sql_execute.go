package v1

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
)

// Relational SQL limits enforced by TerraBase on /sql/execute.
const (
	MaxSQLStatements     = 256
	MaxSQLStatementBytes = 16 << 10
)

// SQL error codes returned by /sql/execute. They also match with errors.Is.
const (
	ErrSQL         ErrorCode = "sql_error"     // 400: SQLite rejected a statement
	ErrSQLConflict ErrorCode = "sql_conflict"  // 409: the transaction conflicted; nothing was applied
	ErrSQLTooLarge ErrorCode = "sql_too_large" // 413: too many statements or bytes
)

// SQLStatement is one statement of a relational SQL request. Args bind
// positional parameters (?, ?NNN); Named binds :name, @name and $name
// parameters; a leading ':', '@' or '$' in a key is removed. Values are nil, bool (bound as 1/0),
// strings, Go integers, floats or json.Number.
type SQLStatement struct {
	SQL   string         `json:"sql"`
	Args  []any          `json:"args,omitempty"`
	Named map[string]any `json:"named,omitempty"`
}

// NamedArg binds a named parameter when passed to ExecuteSQL or QuerySQL.
type NamedArg struct {
	Name  string
	Value any
}

// Named returns a named parameter, keyed as in SQLStatement.Named.
func Named(name string, value any) NamedArg { return NamedArg{Name: name, Value: value} }

// SQLColumn names a result column; DeclType is nil for expressions.
type SQLColumn struct {
	Name     string  `json:"name"`
	DeclType *string `json:"decltype"`
}

// SQLExecResult is the result of one relational SQL statement. Row values
// are nil, int64, float64 or string. Integers decode exactly as int64.
type SQLExecResult struct {
	Columns         []SQLColumn `json:"columns"`
	Rows            [][]any     `json:"rows"`
	RowsAffected    int64       `json:"rows_affected"`
	LastInsertRowID *int64      `json:"last_insert_rowid"`
}

// Maps returns each row keyed by column name. A later duplicate column name
// replaces an earlier one; use Rows when names repeat.
func (r *SQLExecResult) Maps() []map[string]any {
	out := make([]map[string]any, len(r.Rows))
	for i, row := range r.Rows {
		m := make(map[string]any, len(row))
		for j, v := range row {
			if j < len(r.Columns) {
				m[r.Columns[j].Name] = v
			}
		}
		out[i] = m
	}
	return out
}

// SQLError is a statement failure from /sql/execute. It wraps the *APIError,
// so errors.As to *APIError and errors.Is with its ErrorCode still match.
type SQLError struct {
	Code    ErrorCode
	Message string
	// Statement is the 0-based index of the failing statement, if named.
	Statement *int
	// SQLiteCode is the extended SQLite result name, such as SQLITE_CONSTRAINT_PRIMARYKEY.
	SQLiteCode string
	API        *APIError
}

func (e *SQLError) Error() string {
	var b strings.Builder
	fmt.Fprintf(&b, "terrabase: %d %s", e.API.Status, e.Code)
	if e.Message != "" {
		b.WriteString(": " + e.Message)
	}
	if e.Statement != nil {
		fmt.Fprintf(&b, " (statement %d)", *e.Statement)
	}
	if e.SQLiteCode != "" {
		b.WriteString(" [" + e.SQLiteCode + "]")
	}
	return b.String()
}

func (e *SQLError) Unwrap() error { return e.API }

// ExecuteSQL runs one statement in its own transaction. Pass NamedArg values
// for named parameters and any other value for positional ones.
func (d *Database) ExecuteSQL(ctx context.Context, sql string, args ...any) (*SQLExecResult, error) {
	st := SQLStatement{SQL: sql}
	for _, a := range args {
		if n, ok := a.(NamedArg); ok {
			if st.Named == nil {
				st.Named = map[string]any{}
			}
			st.Named[n.Name] = n.Value
		} else {
			st.Args = append(st.Args, a)
		}
	}
	out, err := d.BatchSQL(ctx, []SQLStatement{st})
	if err != nil {
		return nil, err
	}
	return &out[0], nil
}

// QuerySQL runs one statement and returns its rows keyed by column name.
func (d *Database) QuerySQL(ctx context.Context, sql string, args ...any) ([]map[string]any, error) {
	res, err := d.ExecuteSQL(ctx, sql, args...)
	if err != nil {
		return nil, err
	}
	return res.Maps(), nil
}

// BatchSQL runs 1 to 256 statements as one transaction: all apply or none
// do. BEGIN, COMMIT and ROLLBACK are refused. It needs a token allowed to
// write the database. Requests are not retried, because a lost response
// leaves the outcome unknown (ErrCommitUncertain).
func (d *Database) BatchSQL(ctx context.Context, statements []SQLStatement) ([]SQLExecResult, error) {
	if len(statements) == 0 || len(statements) > MaxSQLStatements {
		return nil, fmt.Errorf("terrabase: SQL batch needs 1 to %d statements, got %d", MaxSQLStatements, len(statements))
	}
	wire := make([]SQLStatement, len(statements))
	for i, st := range statements {
		if strings.TrimSpace(st.SQL) == "" || len(st.SQL) > MaxSQLStatementBytes {
			return nil, fmt.Errorf("terrabase: SQL statement %d must be non-empty and at most %d bytes", i, MaxSQLStatementBytes)
		}
		w := SQLStatement{SQL: st.SQL, Args: make([]any, len(st.Args))}
		var ok bool
		for j, v := range st.Args {
			if w.Args[j], ok = sqlArg(v); !ok {
				return nil, fmt.Errorf("terrabase: SQL statement %d arg %d has unsupported type %T", i, j, v)
			}
		}
		if len(st.Named) > 0 {
			w.Named = make(map[string]any, len(st.Named))
		}
		for k, v := range st.Named {
			if w.Named[strings.TrimLeft(k, ":@$")], ok = sqlArg(v); !ok {
				return nil, fmt.Errorf("terrabase: SQL statement %d named arg %q has unsupported type %T", i, k, v)
			}
		}
		wire[i] = w
	}
	p, err := d.path("sql", "execute")
	if err != nil {
		return nil, err
	}
	var out struct {
		Results []SQLExecResult `json:"results"`
	}
	_, err = d.c.call(ctx, request{method: http.MethodPost, path: p}, struct {
		Statements []SQLStatement `json:"statements"`
	}{wire}, &out)
	if apiErr := (*APIError)(nil); errors.As(err, &apiErr) {
		return nil, sqlErrorFrom(apiErr)
	}
	if err != nil {
		return nil, err
	}
	if len(out.Results) != len(statements) {
		return nil, fmt.Errorf("terrabase: SQL response has %d results for %d statements", len(out.Results), len(statements))
	}
	for i := range out.Results {
		for _, row := range out.Results[i].Rows {
			for j, v := range row {
				row[j] = sqlValue(v)
			}
		}
	}
	return out.Results, nil
}

func sqlArg(v any) (any, bool) {
	switch v := v.(type) {
	case nil, string, json.Number, int, int8, int16, int32, int64, uint, uint8, uint16, uint32, uint64, float32, float64:
		return v, true
	case bool:
		if v {
			return 1, true
		}
		return 0, true
	}
	return nil, false
}

func sqlValue(v any) any {
	n, isNum := v.(json.Number)
	if !isNum {
		return v
	}
	if i, err := n.Int64(); err == nil {
		return i
	}
	if f, err := n.Float64(); err == nil {
		return f
	}
	return n
}

func sqlErrorFrom(apiErr *APIError) error {
	switch apiErr.Code {
	case ErrSQL, ErrSQLConflict, ErrSQLTooLarge, ErrForbiddenScope:
	default:
		return apiErr
	}
	var env struct {
		Error struct {
			Statement  *int   `json:"statement"`
			SQLiteCode string `json:"sqlite_code"`
		} `json:"error"`
	}
	_ = json.Unmarshal(apiErr.Body, &env)
	return &SQLError{Code: apiErr.Code, Message: apiErr.Message, Statement: env.Error.Statement, SQLiteCode: env.Error.SQLiteCode, API: apiErr}
}
