package data

import (
	"regexp"
	"strings"

	sdk "terrascale.tech/sdk"
)

const maxSQLCheckBytes = 16 << 10

var (
	sqlParameterPattern = regexp.MustCompile(`\$[0-9]+\b`)
	sqlLimitPattern     = regexp.MustCompile(`(?i)\bLIMIT\s+[0-9]+\b`)
	sqlFromPattern      = regexp.MustCompile(`(?i)\bFROM\s+([A-Za-z_][A-Za-z0-9_]*)\b`)
	sqlWherePattern     = regexp.MustCompile(`(?i)\bWHERE\s+([A-Za-z_][A-Za-z0-9_]*)\b`)
	sqlOrderPattern     = regexp.MustCompile(`(?i)\bORDER\s+BY\s+([A-Za-z_][A-Za-z0-9_]*)\b`)
	sqlWordPattern      = regexp.MustCompile(`[A-Za-z_][A-Za-z0-9_]*`)
)

type sqlSuggestion struct {
	Field  string `json:"field"`
	Reason string `json:"reason"`
}

type sqlAnalysis struct {
	OK             bool            `json:"ok" jsonschema:"Whether the statement is one allowed read-only query."`
	Error          string          `json:"error,omitempty"`
	Collection     string          `json:"collection,omitempty"`
	ParameterSlots int             `json:"parameterSlots"`
	HasLimit       bool            `json:"hasLimit"`
	Explain        string          `json:"explain,omitempty" jsonschema:"EXPLAIN statement that shows the query plan."`
	Suggestions    []sqlSuggestion `json:"suggestions,omitempty" jsonschema:"Fields worth indexing for this query."`
}

func analyzeSQL(statement string) sqlAnalysis {
	statement = strings.TrimSpace(statement)
	if statement == "" {
		return sqlAnalysis{Error: "SQL must not be empty."}
	}
	if len(statement) > maxSQLCheckBytes {
		return sqlAnalysis{Error: "SQL must be 16 KiB or less."}
	}
	// Blank quoted values so keywords and column hints inside values cannot
	// affect the advisory result. Comments and multiple statements are rejected.
	visible, ok := visibleSQL(statement)
	if !ok {
		return sqlAnalysis{Error: "Enter one read-only SQL statement without comments."}
	}
	words := sqlWordPattern.FindAllString(strings.ToUpper(visible), -1)
	if len(words) == 0 {
		return sqlAnalysis{Error: "Only SELECT, WITH, and EXPLAIN statements are allowed."}
	}
	for _, word := range words {
		switch word {
		case "INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "DROP", "ALTER", "ATTACH", "DETACH", "GRANT", "REVOKE", "BEGIN", "COMMIT", "ROLLBACK", "VACUUM", "PRAGMA", "UPSERT", "TRUNCATE", "SAVEPOINT", "TRANSACTION", "REINDEX", "ANALYZE", "INTO", "END":
			return sqlAnalysis{Error: "Only SELECT, WITH, and EXPLAIN statements are allowed."}
		}
	}
	upper := strings.ToUpper(strings.TrimSpace(visible))
	if len(words) > 1 && words[0] == "WITH" && words[1] == "RECURSIVE" {
		return sqlAnalysis{Error: "WITH RECURSIVE is not allowed."}
	}
	if words[0] == "EXPLAIN" && !(strings.HasPrefix(upper, "EXPLAIN QUERY PLAN ") || strings.HasPrefix(upper, "EXPLAIN SELECT ")) {
		return sqlAnalysis{Error: "EXPLAIN may only use EXPLAIN QUERY PLAN or EXPLAIN SELECT."}
	}
	if words[0] != "SELECT" && words[0] != "WITH" && words[0] != "EXPLAIN" {
		return sqlAnalysis{Error: "Only SELECT, WITH, and EXPLAIN statements are allowed."}
	}
	if words[0] == "WITH" && !strings.Contains(upper, "SELECT") {
		return sqlAnalysis{Error: "WITH must contain a SELECT statement."}
	}
	bindings := make(map[string]any)
	for _, parameter := range sqlParameterPattern.FindAllString(visible, -1) {
		bindings[strings.TrimPrefix(parameter, "$")] = nil
	}
	if err := sdk.ValidateSQL(statement, bindings); err != nil {
		return sqlAnalysis{Error: "SQL runtime check rejected this statement: " + err.Error()}
	}
	analysis := sqlAnalysis{OK: true, ParameterSlots: len(sqlParameterPattern.FindAllString(visible, -1)), HasLimit: sqlLimitPattern.MatchString(visible)}
	if match := sqlFromPattern.FindStringSubmatch(visible); len(match) > 1 {
		analysis.Collection = match[1]
	}
	for _, match := range sqlWherePattern.FindAllStringSubmatch(visible, -1) {
		analysis.Suggestions = append(analysis.Suggestions, sqlSuggestion{Field: match[1], Reason: "WHERE filters on " + match[1] + "; consider an index for this field."})
	}
	for _, match := range sqlOrderPattern.FindAllStringSubmatch(visible, -1) {
		analysis.Suggestions = append(analysis.Suggestions, sqlSuggestion{Field: match[1], Reason: "ORDER BY sorts on " + match[1] + "; consider an index for bounded pagination."})
	}
	analysis.Explain = statement
	if words[0] != "EXPLAIN" {
		analysis.Explain = "EXPLAIN QUERY PLAN " + statement
	}
	return analysis
}

func visibleSQL(statement string) (string, bool) {
	visible := []byte(statement)
	quote := byte(0)
	closedStatement := false
	for i := 0; i < len(visible); i++ {
		c := visible[i]
		if quote != 0 {
			visible[i] = ' '
			if c == quote {
				if quote != ']' && i+1 < len(visible) && visible[i+1] == quote {
					i++
					visible[i] = ' '
				} else {
					quote = 0
				}
			}
			continue
		}
		if closedStatement && c != ' ' && c != '\t' && c != '\n' && c != '\r' {
			return "", false
		}
		if i+1 < len(visible) && ((c == '-' && visible[i+1] == '-') || (c == '/' && visible[i+1] == '*')) {
			return "", false
		}
		switch c {
		case '\'', '"', '`':
			quote = c
			visible[i] = ' '
		case '[':
			quote = ']'
			visible[i] = ' '
		case ';':
			closedStatement = true
			visible[i] = ' '
		}
	}
	return string(visible), quote == 0
}
