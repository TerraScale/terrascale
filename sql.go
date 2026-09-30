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

// MetadataFields are immutable columns exposed on every collection table.
var metadataFieldNames = [...]string{"_id", "_rev", "_hlc", "_created", "_updated"}

// MetadataField returns a schema descriptor for one immutable metadata
// column. Metadata columns are available to typed projections and ordering,
// but cannot be declared as user fields.
func MetadataField(name string) (*FieldDef, error) {
	typ := FieldString
	switch name {
	case "_id", "_rev", "_hlc":
	case "_created", "_updated":
		typ = FieldTimestamp
	default:
		return nil, fmt.Errorf("sdk: unknown metadata field %q", name)
	}
	return &FieldDef{Name: name, Type: typ, Required: true, Indexed: true}, nil
}

// MetadataFieldsForQuery returns descriptors for all immutable metadata
// columns in their canonical contract order.
func MetadataFieldsForQuery() ([]FieldDef, error) {
	fields := make([]FieldDef, 0, len(metadataFieldNames))
	for _, name := range metadataFieldNames {
		field, err := MetadataField(name)
		if err != nil {
			return nil, err
		}
		fields = append(fields, *field)
	}
	return fields, nil
}

// MetadataFields returns descriptors for all immutable metadata columns.
func MetadataFields() ([]FieldDef, error) { return MetadataFieldsForQuery() }

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

// QueryBuilder builds a parameterized SELECT over one collection.
type QueryBuilder struct {
	collection string
	columns    []string
	where      *Condition
	orderBy    []string
	limit      int
	hasLimit   bool
	offset     int
	hasOffset  bool
	schema     *CollectionSchema
	buildErr   error
}

// Select starts a query over collection; empty cols selects *.
func Select(collection string, cols ...string) *QueryBuilder {
	return &QueryBuilder{collection: collection, columns: append([]string(nil), cols...)}
}

// SelectSchema starts a SELECT bound to a declared collection schema. A
// schema-bound builder only accepts its declared fields as columns, filters,
// and order keys.
func SelectSchema(schema *CollectionSchema) (*QueryBuilder, error) {
	if schema == nil {
		return nil, fmt.Errorf("sdk: schema cannot be nil")
	}
	if !validSQLIdentifier(schema.Name) {
		return nil, fmt.Errorf("sdk: invalid schema collection name %q", schema.Name)
	}
	seen := map[string]bool{}
	for _, field := range schema.Fields {
		if !validSQLIdentifier(field.Name) || strings.HasPrefix(field.Name, "_") || seen[field.Name] {
			return nil, fmt.Errorf("sdk: invalid or duplicate schema field %q", field.Name)
		}
		seen[field.Name] = true
	}
	copySchema := *schema
	copySchema.Fields = append([]FieldDef(nil), schema.Fields...)
	return &QueryBuilder{collection: schema.Name, schema: &copySchema}, nil
}

// FromSchema is an alias for SelectSchema.
func FromSchema(schema *CollectionSchema) (*QueryBuilder, error) { return SelectSchema(schema) }

// SelectFromSchema is a descriptive alias for SelectSchema.
func SelectFromSchema(schema *CollectionSchema) (*QueryBuilder, error) { return SelectSchema(schema) }

func (q *QueryBuilder) setError(err error) *QueryBuilder {
	if q != nil && q.buildErr == nil {
		q.buildErr = err
	}
	return q
}

// Column adds a declared schema field to the projection.
func (q *QueryBuilder) Column(field *FieldDef) *QueryBuilder {
	if q == nil {
		return q
	}
	if err := q.checkField(field, "column"); err != nil {
		return q.setError(err)
	}
	if len(q.columns) == 0 || (len(q.columns) == 1 && q.columns[0] == "*") {
		q.columns = nil
	}
	q.columns = append(q.columns, field.Name)
	return q
}

// Columns adds several declared schema fields to the projection.
func (q *QueryBuilder) Columns(fields ...*FieldDef) *QueryBuilder {
	for _, field := range fields {
		q = q.Column(field)
	}
	return q
}

func (q *QueryBuilder) typedWhere(field *FieldDef, op string, value any) *QueryBuilder {
	if q == nil {
		return q
	}
	if err := q.checkField(field, "predicate"); err != nil {
		return q.setError(err)
	}
	return q.Where(fieldCond(field.Name, op, value))
}

// WhereEq adds a parameterized equality predicate over a declared field.
func (q *QueryBuilder) WhereEq(field *FieldDef, value any) *QueryBuilder {
	return q.typedWhere(field, "=", value)
}

// WhereNe adds a parameterized inequality predicate over a declared field.
func (q *QueryBuilder) WhereNe(field *FieldDef, value any) *QueryBuilder {
	return q.typedWhere(field, "<>", value)
}

// WhereGt adds a parameterized greater-than predicate over a declared field.
func (q *QueryBuilder) WhereGt(field *FieldDef, value any) *QueryBuilder {
	return q.typedWhere(field, ">", value)
}

// WhereGte adds a parameterized greater-than-or-equal predicate.
func (q *QueryBuilder) WhereGte(field *FieldDef, value any) *QueryBuilder {
	return q.typedWhere(field, ">=", value)
}

// WhereLt adds a parameterized less-than predicate.
func (q *QueryBuilder) WhereLt(field *FieldDef, value any) *QueryBuilder {
	return q.typedWhere(field, "<", value)
}

// WhereLte adds a parameterized less-than-or-equal predicate.
func (q *QueryBuilder) WhereLte(field *FieldDef, value any) *QueryBuilder {
	return q.typedWhere(field, "<=", value)
}

// WhereLike adds a parameterized LIKE predicate over a declared field.
func (q *QueryBuilder) WhereLike(field *FieldDef, value any) *QueryBuilder {
	return q.typedWhere(field, "LIKE", value)
}

// WhereMatch adds an FTS5 MATCH predicate over a declared string/text field.
func (q *QueryBuilder) WhereMatch(field *FieldDef, query string) *QueryBuilder {
	if q == nil {
		return q
	}
	if err := q.checkField(field, "match"); err != nil {
		return q.setError(err)
	}
	if field.Type != FieldString && field.Type != FieldText {
		return q.setError(fmt.Errorf("sdk: FTS MATCH requires a string or text field"))
	}
	return q.Where(Match(field.Name, query))
}

// MatchField is an alias for WhereMatch.
func (q *QueryBuilder) MatchField(field *FieldDef, query string) *QueryBuilder {
	return q.WhereMatch(field, query)
}

// WhereJSON adds a body JSON-path predicate. The supported call forms are
// WhereJSON("$.customer.name", ">", value) and
// WhereJSON(field, "$.nested", ">", value), where field is *FieldDef.
func (q *QueryBuilder) WhereJSON(args ...any) *QueryBuilder {
	if q == nil {
		return q
	}
	var root, path, op string
	var value any
	switch len(args) {
	case 3:
		var ok bool
		path, ok = args[0].(string)
		if !ok {
			return q.setError(fmt.Errorf("sdk: JSON path must be a string"))
		}
		root = "body"
		op, ok = args[1].(string)
		if !ok {
			return q.setError(fmt.Errorf("sdk: JSON operator must be a string"))
		}
		value = args[2]
	case 4:
		switch field := args[0].(type) {
		case *FieldDef:
			if err := q.checkField(field, "JSON predicate"); err != nil {
				return q.setError(err)
			}
			if field.Type != FieldJSON {
				return q.setError(fmt.Errorf("sdk: JSON path predicates require a JSON field"))
			}
			root = field.Name
		case string:
			if !validSQLIdentifier(field) || (q.schema != nil && field != "body" && (!q.hasSchemaField(field) || q.schemaFieldType(field) != FieldJSON)) {
				return q.setError(fmt.Errorf("sdk: invalid or undeclared JSON root field %q", field))
			}
			root = field
		default:
			return q.setError(fmt.Errorf("sdk: JSON root field must be a schema field or identifier"))
		}
		var okPath, okOp bool
		path, okPath = args[1].(string)
		op, okOp = args[2].(string)
		if !okPath || !okOp {
			return q.setError(fmt.Errorf("sdk: JSON path and operator must be strings"))
		}
		value = args[3]
	default:
		return q.setError(fmt.Errorf("sdk: WhereJSON expects path, operator, value"))
	}
	if !validJSONPath(path) {
		return q.setError(fmt.Errorf("sdk: invalid JSON path %q", path))
	}
	if !validComparisonOperator(op) {
		return q.setError(fmt.Errorf("sdk: invalid SQL comparison operator %q", op))
	}
	return q.Where(Condition{expr: jsonRootExpr(root) + " ->> '" + strings.ReplaceAll(path, "'", "''") + "' " + strings.ToUpper(strings.TrimSpace(op)) + " ?", args: []any{value}})
}

// WhereJSONPath is the explicit body-root spelling of WhereJSON.
func (q *QueryBuilder) WhereJSONPath(path, op string, value any) *QueryBuilder {
	return q.WhereJSON(path, op, value)
}

// WhereJSONField adds a JSON path predicate rooted at a declared JSON field.
func (q *QueryBuilder) WhereJSONField(field *FieldDef, path, op string, value any) *QueryBuilder {
	return q.WhereJSON(field, path, op, value)
}

func (q *QueryBuilder) whereJSONOperator(op string, args ...any) *QueryBuilder {
	switch len(args) {
	case 2:
		return q.WhereJSON(args[0], op, args[1])
	case 3:
		return q.WhereJSON(args[0], args[1], op, args[2])
	default:
		return q.setError(fmt.Errorf("sdk: JSON comparison expects path, value or field, path, value"))
	}
}

// JSONEq adds body->>path equality. A schema-field root may optionally be
// supplied as the first argument.
func (q *QueryBuilder) JSONEq(args ...any) *QueryBuilder { return q.whereJSONOperator("=", args...) }

// JSONNe adds body->>path inequality.
func (q *QueryBuilder) JSONNe(args ...any) *QueryBuilder { return q.whereJSONOperator("<>", args...) }

// JSONGt adds body->>path greater-than comparison.
func (q *QueryBuilder) JSONGt(args ...any) *QueryBuilder { return q.whereJSONOperator(">", args...) }

// JSONGte adds body->>path greater-than-or-equal comparison.
func (q *QueryBuilder) JSONGte(args ...any) *QueryBuilder { return q.whereJSONOperator(">=", args...) }

// JSONLt adds body->>path less-than comparison.
func (q *QueryBuilder) JSONLt(args ...any) *QueryBuilder { return q.whereJSONOperator("<", args...) }

// JSONLte adds body->>path less-than-or-equal comparison.
func (q *QueryBuilder) JSONLte(args ...any) *QueryBuilder { return q.whereJSONOperator("<=", args...) }

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
		if q.schema != nil {
			field := strings.Fields(expr)[0]
			if !q.hasSchemaField(field) {
				q.setError(fmt.Errorf("sdk: ORDER BY field %q is not declared by this collection", field))
				continue
			}
		}
		q.orderBy = append(q.orderBy, normalizeOrderExpression(expr))
	}
	return q
}

// OrderByField appends an ORDER BY key from a declared field.
func (q *QueryBuilder) OrderByField(field *FieldDef, direction Direction) *QueryBuilder {
	if q == nil {
		return q
	}
	if err := q.checkField(field, "order_by"); err != nil {
		return q.setError(err)
	}
	if direction != Asc && direction != Desc {
		return q.setError(fmt.Errorf("sdk: invalid ORDER BY direction"))
	}
	dir := "ASC"
	if direction == Desc {
		dir = "DESC"
	}
	q.orderBy = append(q.orderBy, quoteIdent(field.Name)+" "+dir)
	return q
}

// OrderByJSON appends a safe ORDER BY key for a body JSON path. It accepts
// (path, direction), or (jsonField, path, direction) for a schema field root.
func (q *QueryBuilder) OrderByJSON(args ...any) *QueryBuilder {
	if q == nil {
		return q
	}
	root, path := "body", ""
	var direction Direction
	switch len(args) {
	case 2:
		var ok bool
		path, ok = args[0].(string)
		if !ok {
			return q.setError(fmt.Errorf("sdk: JSON path must be a string"))
		}
		direction, ok = args[1].(Direction)
		if !ok {
			return q.setError(fmt.Errorf("sdk: ORDER BY direction has an invalid type"))
		}
	case 3:
		switch field := args[0].(type) {
		case *FieldDef:
			if err := q.checkField(field, "JSON order"); err != nil {
				return q.setError(err)
			}
			if field.Type != FieldJSON {
				return q.setError(fmt.Errorf("sdk: JSON path ordering requires a JSON field"))
			}
			root = field.Name
		case string:
			if !validSQLIdentifier(field) || (q.schema != nil && field != "body" && (!q.hasSchemaField(field) || q.schemaFieldType(field) != FieldJSON)) {
				return q.setError(fmt.Errorf("sdk: invalid or undeclared JSON root field %q", field))
			}
			root = field
		default:
			return q.setError(fmt.Errorf("sdk: JSON root field must be a schema field or identifier"))
		}
		var okPath bool
		path, okPath = args[1].(string)
		if !okPath {
			return q.setError(fmt.Errorf("sdk: JSON path must be a string"))
		}
		var okDirection bool
		direction, okDirection = args[2].(Direction)
		if !okDirection {
			return q.setError(fmt.Errorf("sdk: ORDER BY direction has an invalid type"))
		}
	default:
		return q.setError(fmt.Errorf("sdk: OrderByJSON expects path, direction"))
	}
	if !validJSONPath(path) {
		return q.setError(fmt.Errorf("sdk: invalid JSON path %q", path))
	}
	if direction != Asc && direction != Desc {
		return q.setError(fmt.Errorf("sdk: invalid ORDER BY direction"))
	}
	dir := "ASC"
	if direction == Desc {
		dir = "DESC"
	}
	q.orderBy = append(q.orderBy, jsonRootExpr(root)+" ->> '"+strings.ReplaceAll(path, "'", "''")+"' "+dir)
	return q
}

func jsonRootExpr(root string) string {
	if root == "body" {
		return root
	}
	return quoteIdent(root)
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

func (q *QueryBuilder) checkField(field *FieldDef, context string) error {
	if q.schema == nil {
		return fmt.Errorf("sdk: schema-bound field required; use SelectSchema first")
	}
	if field == nil {
		return fmt.Errorf("sdk: %s field cannot be nil", context)
	}
	if !validSQLIdentifier(field.Name) {
		return fmt.Errorf("sdk: invalid %s field %q", context, field.Name)
	}
	if strings.HasPrefix(field.Name, "_") {
		metadata, err := MetadataField(field.Name)
		if err == nil && metadata.Type == field.Type {
			return nil
		}
		return fmt.Errorf("sdk: unknown metadata %s field %q", context, field.Name)
	}
	for _, declared := range q.schema.Fields {
		if declared == *field {
			return nil
		}
	}
	return fmt.Errorf("sdk: %s field %q is not declared by this collection", context, field.Name)
}

func (q *QueryBuilder) hasSchemaField(name string) bool {
	if _, err := MetadataField(name); err == nil {
		return true
	}
	for _, field := range q.schema.Fields {
		if field.Name == name {
			return true
		}
	}
	return false
}

func (q *QueryBuilder) schemaFieldType(name string) FieldType {
	for _, field := range q.schema.Fields {
		if field.Name == name {
			return field.Type
		}
	}
	return ""
}

// Build renders the parameterized statement and its parameters. Values are
// always placeholders; identifiers and validated JSON paths are structural.
func (q *QueryBuilder) Build() (string, []any, error) {
	if q == nil {
		return "", nil, fmt.Errorf("sdk: query builder cannot be nil")
	}
	if q.buildErr != nil {
		return "", nil, q.buildErr
	}
	if !validSQLIdentifier(q.collection) {
		return "", nil, fmt.Errorf("sdk: query collection must match [A-Za-z_][A-Za-z0-9_]*")
	}
	if strings.TrimSpace(q.collection) == "" {
		return "", nil, fmt.Errorf("sdk: query collection cannot be empty")
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
			if q.schema != nil && col != "*" && !q.hasSchemaField(col) {
				return "", nil, fmt.Errorf("sdk: selected column %q is not declared by this collection", col)
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

func validComparisonOperator(value string) bool {
	switch strings.ToUpper(strings.TrimSpace(value)) {
	case "=", "<>", "!=", ">", ">=", "<", "<=", "LIKE", "MATCH":
		return true
	default:
		return false
	}
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

func validJSONPath(path string) bool {
	if !strings.HasPrefix(path, "$.") || len(path) <= 2 {
		return false
	}
	for _, segment := range strings.Split(path[2:], ".") {
		if !validSQLIdentifier(segment) {
			return false
		}
	}
	return true
}
