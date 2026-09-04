// Typed SQL builder plus raw parameterized SQL (GOAL D-78/D-54).
//
// The query endpoint is POST /api/v1/databases/{id}/sql: read-only SQL over
// extracted columns, `body` as JSON, and `_` metadata columns. Statements are
// limited to select/with/explain; values always travel as parameters, never
// interpolated into the statement text.
package sdk

import (
	"context"
	"fmt"
	"net/url"
	"strings"
)

// SQLResult is the result of a read-only SQL execution.
type SQLResult struct {
	Columns []string         `json:"columns"`
	Rows    []map[string]any `json:"rows"`
}

// Len returns the number of result rows.
func (r *SQLResult) Len() int {
	if r == nil {
		return 0
	}
	return len(r.Rows)
}

// ExecuteSQL runs a raw parameterized statement. Only read-only statements
// (SELECT/WITH/EXPLAIN prefix) are accepted client-side; the server enforces
// the full allow-list (no DDL, DML, pragma, or non-deterministic functions).
func (c *Client) ExecuteSQL(ctx context.Context, databaseID, statement string, params []any, opts ...RequestOption) (*SQLResult, error) {
	if strings.TrimSpace(databaseID) == "" {
		return nil, fmt.Errorf("sdk: database id cannot be empty")
	}
	if !isReadOnlyStatement(statement) {
		return nil, fmt.Errorf("sdk: only read-only SELECT/WITH/EXPLAIN statements are allowed")
	}
	var out SQLResult
	path := "/api/v1/databases/" + url.PathEscape(databaseID) + "/sql"
	body := map[string]any{"statement": statement, "params": params}
	if err := c.do(ctx, "POST", path, nil, body, &out, "sql.execute", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

func isReadOnlyStatement(statement string) bool {
	lowered := strings.ToLower(strings.TrimSpace(statement))
	for _, prefix := range []string{"select", "with", "explain"} {
		if strings.HasPrefix(lowered, prefix) {
			return true
		}
	}
	return false
}

// Condition is one parameterized WHERE fragment.
type Condition struct {
	expr string
	args []any
}

// fieldCond builds "field" <op> ? with a quoted identifier.
func fieldCond(field, op string, value any) Condition {
	return Condition{expr: quoteIdent(field) + " " + op + " ?", args: []any{value}}
}

// Eq matches field equality.
func Eq(field string, value any) Condition { return fieldCond(field, "=", value) }

// Ne matches field inequality.
func Ne(field string, value any) Condition { return fieldCond(field, "<>", value) }

// Gt matches field > value.
func Gt(field string, value any) Condition { return fieldCond(field, ">", value) }

// Gte matches field >= value.
func Gte(field string, value any) Condition { return fieldCond(field, ">=", value) }

// Lt matches field < value.
func Lt(field string, value any) Condition { return fieldCond(field, "<", value) }

// Lte matches field <= value.
func Lte(field string, value any) Condition { return fieldCond(field, "<=", value) }

// Match builds an FTS5 MATCH predicate over field.
func Match(field, query string) Condition {
	return Condition{expr: quoteIdent(field) + " MATCH ?", args: []any{query}}
}

// And conjoins conditions.
func And(conds ...Condition) Condition {
	return joinConditions(" AND ", conds...)
}

// Or disjoins conditions.
func Or(conds ...Condition) Condition {
	return joinConditions(" OR ", conds...)
}

// Not negates a condition.
func Not(cond Condition) Condition {
	return Condition{expr: "NOT (" + cond.expr + ")", args: append([]any(nil), cond.args...)}
}

func joinConditions(sep string, conds ...Condition) Condition {
	parts := make([]string, 0, len(conds))
	var args []any
	for _, cond := range conds {
		if cond.expr == "" {
			continue
		}
		parts = append(parts, "("+cond.expr+")")
		args = append(args, cond.args...)
	}
	return Condition{expr: strings.Join(parts, sep), args: args}
}

func quoteIdent(name string) string {
	return `"` + strings.ReplaceAll(name, `"`, `""`) + `"`
}

// QueryBuilder builds a parameterized SELECT over one collection.
type QueryBuilder struct {
	collection string
	columns    []string
	where      *Condition
	orderBy    []string
	limit      int
	hasLimit   bool
}

// Select starts a query over collection; empty cols selects *.
func Select(collection string, cols ...string) *QueryBuilder {
	return &QueryBuilder{collection: collection, columns: append([]string(nil), cols...)}
}

// Where AND-combines cond into the query predicate.
func (q *QueryBuilder) Where(cond Condition) *QueryBuilder {
	if cond.expr == "" {
		return q
	}
	if q.where == nil {
		c := cond
		q.where = &c
		return q
	}
	combined := And(*q.where, cond)
	q.where = &combined
	return q
}

// OrderBy appends ORDER BY expressions (e.g. "created_at DESC").
func (q *QueryBuilder) OrderBy(exprs ...string) *QueryBuilder {
	q.orderBy = append(q.orderBy, exprs...)
	return q
}

// Limit caps result rows.
func (q *QueryBuilder) Limit(n int) *QueryBuilder {
	q.limit = n
	q.hasLimit = true
	return q
}

// Build renders the parameterized statement and its parameters. Values are
// always placeholders; identifiers are quoted.
func (q *QueryBuilder) Build() (string, []any, error) {
	if strings.TrimSpace(q.collection) == "" {
		return "", nil, fmt.Errorf("sdk: query collection cannot be empty")
	}
	if q.hasLimit && q.limit < 0 {
		return "", nil, fmt.Errorf("sdk: limit cannot be negative")
	}
	cols := "*"
	if len(q.columns) > 0 {
		quoted := make([]string, 0, len(q.columns))
		for _, col := range q.columns {
			if strings.TrimSpace(col) == "" {
				return "", nil, fmt.Errorf("sdk: selected columns cannot be empty")
			}
			quoted = append(quoted, quoteIdent(col))
		}
		cols = strings.Join(quoted, ", ")
	}
	var sb strings.Builder
	sb.WriteString("SELECT " + cols + " FROM " + quoteIdent(q.collection))
	var params []any
	if q.where != nil && q.where.expr != "" {
		sb.WriteString(" WHERE " + q.where.expr)
		params = append(params, q.where.args...)
	}
	if len(q.orderBy) > 0 {
		sb.WriteString(" ORDER BY " + strings.Join(q.orderBy, ", "))
	}
	if q.hasLimit {
		sb.WriteString(" LIMIT ?")
		params = append(params, q.limit)
	}
	return sb.String(), params, nil
}

// ExecuteQuery executes a built query through the read-only SQL endpoint.
func (c *Client) ExecuteQuery(ctx context.Context, databaseID string, qb *QueryBuilder, opts ...RequestOption) (*SQLResult, error) {
	if qb == nil {
		return nil, fmt.Errorf("sdk: query builder cannot be nil")
	}
	statement, params, err := qb.Build()
	if err != nil {
		return nil, err
	}
	return c.ExecuteSQL(ctx, databaseID, statement, params, opts...)
}
