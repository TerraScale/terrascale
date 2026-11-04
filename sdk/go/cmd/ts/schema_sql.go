package main

import (
	"fmt"
	"strings"
	"unicode"
)

// schemaToken retains source offsets for column DDL while comparing SQL without
// whitespace, comments or identifier quoting differences. String values stay exact.
type schemaToken struct {
	raw, key   string
	start, end int
}

func schemaTokens(source string) ([]schemaToken, error) {
	var out []schemaToken
	for i := 0; i < len(source); {
		start := i
		switch {
		case unicode.IsSpace(rune(source[i])):
			i++
			continue
		case strings.HasPrefix(source[i:], "--"):
			if n := strings.IndexByte(source[i:], '\n'); n >= 0 {
				i += n + 1
			} else {
				i = len(source)
			}
			continue
		case strings.HasPrefix(source[i:], "/*"):
			n := strings.Index(source[i+2:], "*/")
			if n < 0 {
				return nil, fmt.Errorf("unterminated SQL comment")
			}
			i += n + 4
			continue
		case strings.ContainsRune("'\"`[", rune(source[i])):
			quote := source[i]
			close := quote
			if close == '[' {
				close = ']'
			}
			i++
			var value strings.Builder
			closed := false
			for i < len(source) {
				if source[i] == close {
					i++
					if close != ']' && i < len(source) && source[i] == close {
						value.WriteByte(close)
						i++
						continue
					}
					closed = true
					break
				}
				value.WriteByte(source[i])
				i++
			}
			if !closed {
				return nil, fmt.Errorf("unterminated SQL quote")
			}
			key := "w:" + strings.ToLower(value.String())
			if quote == '\'' {
				key = "s:" + value.String()
			}
			out = append(out, schemaToken{source[start:i], key, start, i})
			continue
		case source[i] == '_' || source[i] >= 128 || source[i] >= 'a' && source[i] <= 'z' || source[i] >= 'A' && source[i] <= 'Z' || source[i] >= '0' && source[i] <= '9':
			i++
			for i < len(source) && (source[i] == '_' || source[i] == '$' || source[i] >= 128 || source[i] >= 'a' && source[i] <= 'z' || source[i] >= 'A' && source[i] <= 'Z' || source[i] >= '0' && source[i] <= '9') {
				i++
			}
		default:
			i++
		}
		out = append(out, schemaToken{source[start:i], "w:" + strings.ToLower(source[start:i]), start, i})
	}
	return out, nil
}

func tokenKeys(tokens []schemaToken) string {
	var b strings.Builder
	for _, t := range tokens {
		fmt.Fprintf(&b, "%d:%s", len(t.key), t.key)
	}
	return b.String()
}

func sameSchemaSQL(a, b string) bool {
	x, err := schemaTokens(strings.TrimSuffix(strings.TrimSpace(a), ";"))
	if err != nil {
		return false
	}
	y, err := schemaTokens(strings.TrimSuffix(strings.TrimSpace(b), ";"))
	return err == nil && tokenKeys(x) == tokenKeys(y)
}

// schemaStatements accepts declarative CREATEs only. A trigger body is one
// statement, including its semicolons and nested CASE expressions.
func schemaStatements(source string) ([]string, error) {
	tokens, err := schemaTokens(source)
	if err != nil {
		return nil, err
	}
	var statements []string
	for len(tokens) > 0 {
		if tokens[0].raw == ";" {
			tokens = tokens[1:]
			continue
		}
		if len(tokens) < 3 || !strings.EqualFold(tokens[0].raw, "CREATE") {
			return nil, fmt.Errorf("schema.sql accepts only CREATE TABLE, INDEX, VIEW and TRIGGER; use ts migrate for data changes")
		}
		kind := 1
		if strings.EqualFold(tokens[kind].raw, "UNIQUE") {
			kind++
		}
		if kind >= len(tokens) {
			return nil, fmt.Errorf("incomplete CREATE statement")
		}
		switch strings.ToLower(tokens[kind].raw) {
		case "table", "index", "view", "trigger":
		default:
			return nil, fmt.Errorf("schema.sql accepts only ordinary tables, indexes, views and triggers")
		}
		trigger := strings.EqualFold(tokens[kind].raw, "TRIGGER")
		depth, end := 0, len(tokens)
		for i, t := range tokens {
			if trigger {
				switch strings.ToUpper(t.raw) {
				case "BEGIN", "CASE":
					depth++
				case "END":
					depth--
				}
			}
			if t.raw == ";" && depth == 0 {
				end = i + 1
				break
			}
		}
		statement := strings.TrimSpace(source[tokens[0].start:tokens[end-1].end])
		if len(statement) > 16384 {
			return nil, fmt.Errorf("schema statement exceeds 16 KiB")
		}
		statements = append(statements, statement)
		tokens = tokens[end:]
	}
	return statements, nil
}

type schemaColumn struct{ name, definition, key string }

// tableParts keeps constraints and table options separate from column entries.
func tableParts(source string) ([]schemaColumn, string, error) {
	tokens, err := schemaTokens(source)
	if err != nil {
		return nil, "", err
	}
	open := -1
	for i, t := range tokens {
		if t.raw == "(" {
			open = i
			break
		}
	}
	if open < 0 {
		return nil, "", fmt.Errorf("CREATE TABLE needs explicit column definitions")
	}
	var columns []schemaColumn
	var constraints strings.Builder
	depth, start := 1, open+1
	for i := start; i < len(tokens); i++ {
		switch tokens[i].raw {
		case "(":
			depth++
		case ")":
			depth--
		}
		if depth == 0 || depth == 1 && tokens[i].raw == "," {
			part := tokens[start:i]
			if len(part) == 0 {
				return nil, "", fmt.Errorf("empty table definition")
			}
			switch strings.ToUpper(part[0].raw) {
			case "PRIMARY", "UNIQUE", "CHECK", "FOREIGN", "CONSTRAINT":
				constraints.WriteString(tokenKeys(part))
			default:
				columns = append(columns, schemaColumn{part[0].raw, source[part[0].start:part[len(part)-1].end], tokenKeys(part)})
			}
			start = i + 1
		}
		if depth == 0 {
			constraints.WriteString(tokenKeys(tokens[i+1:]))
			return columns, constraints.String(), nil
		}
	}
	return nil, "", fmt.Errorf("unclosed table definition")
}

func alterSchemaTable(before, after schemaEntity) ([]string, error) {
	old, oldConstraints, err := tableParts(before.SQL)
	if err != nil {
		return nil, err
	}
	desired, constraints, err := tableParts(after.SQL)
	if err != nil {
		return nil, err
	}
	unsupported := fmt.Errorf("table %q changes an existing column, constraint, column order or table option: use an explicit ts migrate migration", before.Name)
	if constraints != oldConstraints {
		return nil, unsupported
	}
	oldByName, newByName := map[string]schemaColumn{}, map[string]schemaColumn{}
	columnKey := func(c schemaColumn) string { ts, _ := schemaTokens(c.name); return tokenKeys(ts) }
	for _, c := range old {
		oldByName[columnKey(c)] = c
	}
	for _, c := range desired {
		newByName[columnKey(c)] = c
	}
	var statements []string
	var remaining []string
	for _, c := range old {
		key := columnKey(c)
		if _, ok := newByName[key]; !ok {
			statements = append(statements, "ALTER TABLE "+quoteSchemaName(before.Name)+" DROP COLUMN "+c.name)
		} else {
			remaining = append(remaining, key)
		}
	}
	for _, c := range desired {
		key := columnKey(c)
		if prev, ok := oldByName[key]; ok {
			if prev.key != c.key {
				return nil, unsupported
			}
		} else {
			statements = append(statements, "ALTER TABLE "+quoteSchemaName(before.Name)+" ADD COLUMN "+c.definition)
			remaining = append(remaining, key)
		}
	}
	for i, c := range desired {
		if i >= len(remaining) || columnKey(c) != remaining[i] {
			return nil, unsupported
		}
	}
	return statements, nil
}

func quoteSchemaName(name string) string { return `"` + strings.ReplaceAll(name, `"`, `""`) + `"` }
