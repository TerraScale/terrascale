package data

import (
	"bytes"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"strings"
	"unicode/utf8"

	sdk "terrascale.tech/sdk"
)

// Bounds that keep a large result readable and the page small. The raw JSON
// view holds the same rows the table shows.
const (
	maxResultRows    = 200
	maxResultColumns = 24
	maxCellRunes     = 160
)

// sqlResult is a query result laid out as a table: the document ID, then one
// column per top-level field in the order the rows first use it. Rows whose
// value is not a JSON object show it in a "value" column.
type sqlResult struct {
	Columns []string
	Rows    []sqlResultRow
	// Matched counts every row the query matched; the table may show fewer.
	Matched int
	// HiddenColumns counts the fields beyond maxResultColumns.
	HiddenColumns int
	Explain       *sdk.SQLExplainResponse
	// JSON is the shown rows, indented, for the raw view.
	JSON string
}

type sqlResultRow struct {
	ID    string
	Cells []sqlCell
}

// sqlCell is one formatted value. Kind is the JSON kind (string, number,
// bool, null, object, array) or missing when the row lacks the column.
type sqlCell struct {
	Text  string
	Kind  string
	Title string
}

// newSQLResult lays out rows for display; matched is the total the query
// reported, at least len(rows).
func newSQLResult(rows []documentView, matched int) sqlResult {
	result := sqlResult{Matched: max(matched, len(rows))}
	rows = rows[:min(len(rows), maxResultRows)]
	fields := make([]map[string]jsontext.Value, len(rows))
	index := make(map[string]int)
	var columns []string
	for i, row := range rows {
		names, values, ok := objectMembers(row.Value)
		if !ok {
			names, values = []string{"value"}, []jsontext.Value{row.Value}
		}
		fields[i] = make(map[string]jsontext.Value, len(names))
		for j, name := range names {
			fields[i][name] = values[j]
			if _, seen := index[name]; !seen {
				index[name] = len(columns)
				columns = append(columns, name)
			}
		}
	}
	if len(columns) > maxResultColumns {
		result.HiddenColumns = len(columns) - maxResultColumns
		columns = columns[:maxResultColumns]
	}
	result.Columns = columns
	for i, row := range rows {
		cells := make([]sqlCell, len(columns))
		for j, column := range columns {
			value, ok := fields[i][column]
			if !ok {
				cells[j] = sqlCell{Kind: "missing"}
				continue
			}
			cells[j] = formatCell(value)
		}
		result.Rows = append(result.Rows, sqlResultRow{ID: row.ID, Cells: cells})
	}
	result.JSON = rawRows(rows)
	return result
}

// Truncated reports whether the query matched more rows than the table shows.
func (r sqlResult) Truncated() bool { return r.Matched > len(r.Rows) }

// objectMembers returns the members of a JSON object in document order.
func objectMembers(value jsontext.Value) ([]string, []jsontext.Value, bool) {
	if value.Kind() != '{' {
		return nil, nil, false
	}
	decoder := jsontext.NewDecoder(bytes.NewReader(value))
	if _, err := decoder.ReadToken(); err != nil {
		return nil, nil, false
	}
	var names []string
	var values []jsontext.Value
	for decoder.PeekKind() == '"' {
		name, err := decoder.ReadToken()
		if err != nil {
			return nil, nil, false
		}
		// The token is only valid until the next read.
		names = append(names, name.String())
		member, err := decoder.ReadValue()
		if err != nil {
			return nil, nil, false
		}
		values = append(values, member.Clone())
	}
	if _, err := decoder.ReadToken(); err != nil {
		return nil, nil, false
	}
	return names, values, true
}

// formatCell shows strings without quotes and other values as compact JSON,
// cut to maxCellRunes; Title keeps a longer cut for a tooltip.
func formatCell(value jsontext.Value) sqlCell {
	cell := sqlCell{Kind: kindName(value.Kind())}
	compact := value.Clone()
	if err := compact.Compact(); err != nil {
		compact = value
	}
	text := compact.String()
	if cell.Kind == "string" {
		_ = json.Unmarshal(value, &text)
	}
	if utf8.RuneCountInString(text) > maxCellRunes {
		cell.Title = truncateRunes(text, 4*maxCellRunes)
		text = truncateRunes(text, maxCellRunes)
	}
	cell.Text = text
	return cell
}

func truncateRunes(text string, limit int) string {
	if utf8.RuneCountInString(text) <= limit {
		return text
	}
	runes := []rune(text)
	return strings.TrimRight(string(runes[:limit-1]), " ") + "…"
}

func kindName(kind jsontext.Kind) string {
	switch kind {
	case '"':
		return "string"
	case '0':
		return "number"
	case 't', 'f':
		return "bool"
	case 'n':
		return "null"
	case '{':
		return "object"
	case '[':
		return "array"
	default:
		return "missing"
	}
}

// rawRows renders rows as indented JSON in the shape the API returns.
func rawRows(rows []documentView) string {
	encoded, err := json.Marshal(rows, jsontext.WithIndent("  "))
	if err != nil {
		return ""
	}
	return string(encoded)
}
