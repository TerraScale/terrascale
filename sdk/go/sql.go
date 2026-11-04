// Pure parameterized SQL validation and typed query construction.
// Native PostgreSQL execution requires its producer-owned contract.
package sdk

import (
	"fmt"
	"strconv"
	"strings"
)

// SQLParameters is the named/ordinal binding object used for pure SQL validation.
// Ordinal keys are decimal strings such as "1" and named keys
// are plain SQL identifiers.
type SQLParameters = map[string]any

// ValidateSQL checks a read-only statement and its positional or named bindings.
// Values are validated as parameters and are never parsed as SQL.
func ValidateSQL(statement string, params any) error {
	tokens, err := tokenizeSQL(statement)
	if err != nil {
		return err
	}
	if len(tokens) == 0 {
		return sqlError("SQL must not be empty")
	}

	first := strings.ToLower(tokens[0].text)
	bodyStart := 1
	switch first {
	case "select":
	case "with":
	case "explain":
		if len(tokens) > 1 && strings.EqualFold(tokens[1].text, "query") {
			if len(tokens) < 3 || !strings.EqualFold(tokens[2].text, "plan") {
				return sqlError("EXPLAIN may only use EXPLAIN QUERY PLAN or EXPLAIN SELECT")
			}
			bodyStart = 3
		}
		if len(tokens) <= bodyStart ||
			(!strings.EqualFold(tokens[bodyStart].text, "select") && !strings.EqualFold(tokens[bodyStart].text, "with")) {
			return sqlError("Only EXPLAIN SELECT and EXPLAIN QUERY PLAN SELECT are allowed")
		}
	default:
		return sqlError("Only SELECT, WITH, and EXPLAIN statements are allowed")
	}

	for i, token := range tokens {
		if i < bodyStart || token.quotedString {
			continue
		}
		lowered := strings.ToLower(token.text)
		if forbiddenSQLKeyword(lowered) {
			return sqlError("SQL contains a DDL, DML, or transaction keyword")
		}
		if strings.HasPrefix(lowered, "sqlite_") || strings.HasPrefix(lowered, "pragma_") {
			return sqlError("Internal SQLite names and pragma functions are not allowed")
		}
		if token.function && forbiddenSQLFunction(lowered) {
			return sqlError("Non-deterministic or unsafe SQL functions are not allowed")
		}
		if lowered == "with" && i+1 < len(tokens) && strings.EqualFold(tokens[i+1].text, "recursive") && !tokens[i+1].quotedString {
			return sqlError("WITH RECURSIVE is not allowed by the read-only SQL contract")
		}
		if (lowered == "current_date" || lowered == "current_time" || lowered == "current_timestamp") && !token.function {
			return sqlError("Non-deterministic or unsafe SQL functions are not allowed")
		}
	}
	return validateBindings(tokens, params)
}

func sqlError(message string) error { return fmt.Errorf("sdk: invalid SQL: %s", message) }

func forbiddenSQLKeyword(value string) bool {
	switch value {
	case "attach", "alter", "analyze", "begin", "commit", "create", "delete", "detach", "drop", "end", "insert", "into", "pragma", "reindex", "replace", "rollback", "savepoint", "transaction", "truncate", "update", "vacuum", "upsert":
		return true
	default:
		return false
	}
}

func forbiddenSQLFunction(value string) bool {
	switch value {
	case "random", "randomblob", "changes", "total_changes", "last_insert_rowid", "load_extension", "readfile", "writefile", "datetime", "date", "time", "strftime", "julianday", "unixepoch", "current_date", "current_time", "current_timestamp", "sqlite_version", "sqlite_source_id", "sqlite_compileoption_get", "sqlite_compileoption_used", "pragma_table_info", "pragma_index_info", "pragma_index_list", "json_each", "json_tree":
		return true
	default:
		return false
	}
}

type sqlToken struct {
	text         string
	quotedString bool
	function     bool
	parameter    bool
}

func tokenizeSQL(statement string) ([]sqlToken, error) {
	if len(statement) > 1<<20 {
		return nil, sqlError("SQL exceeds the 1 MiB query text limit")
	}
	var tokens []sqlToken
	b := []byte(statement)
	for i := 0; i < len(b); {
		switch b[i] {
		case ' ', '\t', '\r', '\n', '\f':
			i++
		case ';':
			i++
			for i < len(b) && (b[i] == ' ' || b[i] == '\t' || b[i] == '\r' || b[i] == '\n' || b[i] == '\f') {
				i++
			}
			if i != len(b) {
				return nil, sqlError("Multiple SQL statements are not allowed")
			}
		case '-':
			if i+1 < len(b) && b[i+1] == '-' {
				return nil, sqlError("SQL comments are not allowed")
			}
			tokens = append(tokens, sqlToken{text: "-"})
			i++
		case '/':
			if i+1 < len(b) && b[i+1] == '*' {
				return nil, sqlError("SQL comments are not allowed")
			}
			tokens = append(tokens, sqlToken{text: "/"})
			i++
		case '\'', '"', '`', '[':
			quote := b[i]
			end := quote
			if quote == '[' {
				end = ']'
			}
			i++
			start := i
			closed := false
			for i < len(b) {
				if b[i] == end {
					if end != ']' && i+1 < len(b) && b[i+1] == end {
						i += 2
						continue
					}
					i++
					closed = true
					break
				}
				i++
			}
			if !closed {
				return nil, sqlError("SQL contains an unterminated quoted value")
			}
			quotedString := quote == '\''
			text := string(b[start : i-1])
			tokens = append(tokens, sqlToken{text: text, quotedString: quotedString})
		case '$':
			start := i
			i++
			if i < len(b) && b[i] >= '0' && b[i] <= '9' {
				for i < len(b) && b[i] >= '0' && b[i] <= '9' {
					i++
				}
			} else if i < len(b) && isSQLIdentStartByte(b[i]) {
				for i < len(b) && isSQLIdentByte(b[i]) {
					i++
				}
			} else {
				return nil, sqlError("Invalid named or numbered parameter")
			}
			tokens = append(tokens, sqlToken{text: string(b[start:i]), parameter: true})
		case '?':
			start := i
			i++
			for i < len(b) && b[i] >= '0' && b[i] <= '9' {
				i++
			}
			if i < len(b) && ((b[i] >= 'a' && b[i] <= 'z') || (b[i] >= 'A' && b[i] <= 'Z') || b[i] == '_') {
				return nil, sqlError("Invalid positional parameter")
			}
			tokens = append(tokens, sqlToken{text: string(b[start:i]), parameter: true})
		case ':', '@':
			start := i
			i++
			if i >= len(b) || !isSQLIdentStartByte(b[i]) {
				return nil, sqlError("Invalid named parameter")
			}
			for i < len(b) && isSQLIdentByte(b[i]) {
				i++
			}
			tokens = append(tokens, sqlToken{text: string(b[start:i]), parameter: true})
		default:
			if b[i] < 0x20 {
				return nil, sqlError("SQL contains an invalid control character")
			}
			if isSQLIdentByte(b[i]) {
				start := i
				i++
				for i < len(b) && isSQLIdentByte(b[i]) {
					i++
				}
				text := string(b[start:i])
				lookahead := i
				for lookahead < len(b) && (b[lookahead] == ' ' || b[lookahead] == '\t' || b[lookahead] == '\r' || b[lookahead] == '\n' || b[lookahead] == '\f') {
					lookahead++
				}
				function := lookahead < len(b) && b[lookahead] == '('
				tokens = append(tokens, sqlToken{text: text, function: function})
			} else {
				tokens = append(tokens, sqlToken{text: string(b[i])})
				i++
			}
		}
	}
	return tokens, nil
}

func isSQLIdentByte(value byte) bool {
	return value >= 'a' && value <= 'z' || value >= 'A' && value <= 'Z' || value >= '0' && value <= '9' || value == '_'
}

func isSQLIdentStartByte(value byte) bool {
	return value >= 'a' && value <= 'z' || value >= 'A' && value <= 'Z' || value == '_'
}

// canonicalSQLParameters normalizes positional and named inputs for SQL
// validation. Positional slices are converted to decimal ordinal keys.
func canonicalSQLParameters(params any) (map[string]any, error) {
	bindings := make(map[string]any)
	switch values := params.(type) {
	case nil:
		return bindings, nil
	case []any:
		for i, value := range values {
			bindings[strconv.Itoa(i+1)] = value
		}
	case map[string]any:
		for name, value := range values {
			if !validSQLBindingName(name) {
				return nil, sqlError(fmt.Sprintf("invalid parameter name %q", name))
			}
			bindings[name] = value
		}
	default:
		return nil, sqlError("parameters must be a positional []any or named map[string]any")
	}
	return bindings, nil
}

func validSQLBindingName(name string) bool {
	if name == "" {
		return false
	}
	if name[0] >= '0' && name[0] <= '9' {
		for i := 1; i < len(name); i++ {
			if name[i] < '0' || name[i] > '9' {
				return false
			}
		}
		return name != "0"
	}
	if !isSQLIdentStartByte(name[0]) {
		return false
	}
	for i := 1; i < len(name); i++ {
		if !isSQLIdentByte(name[i]) {
			return false
		}
	}
	return true
}

func validateBindings(tokens []sqlToken, params any) error {
	bindings, err := canonicalSQLParameters(params)
	if err != nil {
		return err
	}
	nextUnnumbered := 1
	seen := map[string]bool{}
	for _, token := range tokens {
		if !token.parameter {
			continue
		}
		name := strings.TrimLeft(token.text, "?$:@")
		if token.text == "?" {
			name = strconv.Itoa(nextUnnumbered)
			nextUnnumbered++
		} else if name == "" || !validSQLBindingName(name) {
			return sqlError("Invalid named or numbered parameter")
		}
		if name[0] >= '0' && name[0] <= '9' {
			number, parseErr := strconv.Atoi(name)
			if parseErr != nil || number <= 0 {
				return sqlError("Parameter numbering starts at 1")
			}
			if number >= nextUnnumbered {
				nextUnnumbered = number + 1
			}
		}
		seen[name] = true
	}
	for name := range seen {
		if _, ok := bindings[name]; !ok {
			return sqlError(fmt.Sprintf("SQL parameter %q has no value", name))
		}
	}
	for name := range bindings {
		if !seen[name] {
			return sqlError(fmt.Sprintf("SQL parameter %q is not referenced", name))
		}
	}
	return nil
}

// Condition is one parameterized WHERE fragment.
type Condition struct {
	expr string
	args []any
}

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

// Like matches a field with SQLite LIKE semantics.
func Like(field string, value any) Condition { return fieldCond(field, "LIKE", value) }

// Match builds an FTS5 MATCH predicate over field.
func Match(field, query string) Condition {
	return Condition{expr: quoteIdent(field) + " MATCH ?", args: []any{query}}
}

// And conjoins conditions.
func And(conds ...Condition) Condition { return joinConditions(" AND ", conds...) }

// Or disjoins conditions.
func Or(conds ...Condition) Condition { return joinConditions(" OR ", conds...) }

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

func quoteIdent(name string) string { return `"` + strings.ReplaceAll(name, `"`, `""`) + `"` }

// Direction controls a typed ORDER BY key.
type Direction uint8

const (
	Asc Direction = iota
	Desc
	OrderAsc  = Asc
	OrderDesc = Desc
)

// QueryBuilder builds a parameterized SELECT over one table.
type QueryBuilder struct {
	table     string
	columns   []string
	where     *Condition
	orderBy   []string
	limit     int
	hasLimit  bool
	offset    int
	hasOffset bool
	buildErr  error
}

// Select starts a query over table; empty cols selects *.
func Select(table string, cols ...string) *QueryBuilder {
	return &QueryBuilder{table: table, columns: append([]string(nil), cols...)}
}

func (q *QueryBuilder) setError(err error) *QueryBuilder {
	if q != nil && q.buildErr == nil {
		q.buildErr = err
	}
	return q
}

// Where AND-combines cond into the query predicate.
func (q *QueryBuilder) Where(cond Condition) *QueryBuilder {
	if q == nil || cond.expr == "" {
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

// OrderBy appends safe identifier [ASC|DESC] expressions. Existing callers
// may continue passing expressions such as "created_at DESC".
func (q *QueryBuilder) OrderBy(exprs ...string) *QueryBuilder {
	if q == nil {
		return q
	}
	for _, expr := range exprs {
		if !validOrderExpression(expr) {
			q.setError(fmt.Errorf("sdk: invalid ORDER BY expression %q", expr))
			continue
		}
		q.orderBy = append(q.orderBy, normalizeOrderExpression(expr))
	}
	return q
}

// Limit caps result rows.
func (q *QueryBuilder) Limit(n int) *QueryBuilder {
	if q != nil {
		q.limit = n
		q.hasLimit = true
	}
	return q
}

// Offset skips result rows before returning the page.
func (q *QueryBuilder) Offset(n int) *QueryBuilder {
	if q != nil {
		q.offset = n
		q.hasOffset = true
	}
	return q
}

// Paginate applies an offset and limit while keeping both as bound
// parameters.
func (q *QueryBuilder) Paginate(offset, limit int) *QueryBuilder {
	return q.Offset(offset).Limit(limit)
}

// Build renders the parameterized statement and its parameters. Values are
// always placeholders; identifiers are structural.
func (q *QueryBuilder) Build() (string, []any, error) {
	if q == nil {
		return "", nil, fmt.Errorf("sdk: query builder cannot be nil")
	}
	if q.buildErr != nil {
		return "", nil, q.buildErr
	}
	if !validSQLIdentifier(q.table) {
		return "", nil, fmt.Errorf("sdk: query table must match [A-Za-z_][A-Za-z0-9_]*")
	}
	if q.hasLimit && q.limit < 0 {
		return "", nil, fmt.Errorf("sdk: limit cannot be negative")
	}
	if q.hasOffset && q.offset < 0 {
		return "", nil, fmt.Errorf("sdk: offset cannot be negative")
	}
	cols := "*"
	if len(q.columns) > 0 {
		quoted := make([]string, 0, len(q.columns))
		for _, col := range q.columns {
			if strings.TrimSpace(col) == "" {
				return "", nil, fmt.Errorf("sdk: selected columns cannot be empty")
			}
			if col != "*" && !validSQLIdentifier(col) {
				return "", nil, fmt.Errorf("sdk: selected columns must be valid identifiers")
			}
			if col == "*" {
				quoted = append(quoted, col)
				continue
			}
			quoted = append(quoted, quoteIdent(col))
		}
		cols = strings.Join(quoted, ", ")
	}
	var sb strings.Builder
	sb.WriteString("SELECT " + cols + " FROM " + quoteIdent(q.table))
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
	if q.hasOffset {
		if !q.hasLimit {
			sb.WriteString(" LIMIT -1")
		}
		sb.WriteString(" OFFSET ?")
		params = append(params, q.offset)
	}
	return sb.String(), params, nil
}

func validSQLIdentifier(value string) bool {
	if value == "" || !((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z') || value[0] == '_') {
		return false
	}
	for i := 1; i < len(value); i++ {
		if !isSQLIdentByte(value[i]) {
			return false
		}
	}
	return true
}

func validOrderExpression(value string) bool {
	parts := strings.Fields(value)
	if len(parts) == 0 || len(parts) > 2 || !validSQLIdentifier(parts[0]) {
		return false
	}
	return len(parts) == 1 || strings.EqualFold(parts[1], "ASC") || strings.EqualFold(parts[1], "DESC")
}

func normalizeOrderExpression(value string) string {
	// Keep the original spelling for compatibility with the original builder;
	// validOrderExpression has already excluded SQL fragments and operators.
	return strings.TrimSpace(value)
}
