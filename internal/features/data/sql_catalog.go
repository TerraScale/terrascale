package data

import (
	"context"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"maps"
	"slices"
	"strconv"
	"strings"
	"sync"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
	v1 "terrascale.tech/sdk/v1"
)

// sqlKeywords is the TerraBase SQL dialect, read from its query parser
// (terrabase src/TerraScale.TerraBase/Features/Management/Queries/SqlSubset.cs):
// SELECT * | field, … FROM collection [WHERE …] [ORDER BY field [ASC|DESC], …]
// [LIMIT n], where a condition compares a field with =, !=, <, <=, >, >=,
// [NOT] IN (…) or IS [NOT] NULL and conditions join with AND, OR and NOT.
// Values are single-quoted strings, numbers, TRUE and FALSE. The dialect has
// no functions, so the editor offers none.
var sqlKeywords = []string{"SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "ORDER BY", "ASC", "DESC", "LIMIT", "TRUE", "FALSE"}

// sqlUnsupported maps words of the SQL the parser reads but the dialect
// rejects to the parser's reason.
var sqlUnsupported = map[string]string{
	"WITH":      "WITH is not supported.",
	"UNION":     "UNION, INTERSECT and EXCEPT are not supported.",
	"INTERSECT": "UNION, INTERSECT and EXCEPT are not supported.",
	"EXCEPT":    "UNION, INTERSECT and EXCEPT are not supported.",
	"OFFSET":    "OFFSET and FETCH are not supported.",
	"FETCH":     "OFFSET and FETCH are not supported.",
	"DISTINCT":  "DISTINCT and TOP are not supported.",
	"TOP":       "DISTINCT and TOP are not supported.",
	"GROUP":     "GROUP BY and HAVING are not supported.",
	"HAVING":    "GROUP BY and HAVING are not supported.",
	"JOIN":      "Joins and multiple collections are not supported.",
	"AS":        "Aliases are not supported.",
	"LIKE":      "LIKE is not supported in WHERE.",
	"ILIKE":     "LIKE is not supported in WHERE.",
	"BETWEEN":   "BETWEEN is not supported in WHERE.",
	"CASE":      "CASE is not supported.",
	"CAST":      "CAST is not supported.",
	"EXPLAIN":   "Only SELECT statements run on TerraBase.",
}

const (
	// sqlCatalogSample is how many documents a collection without a schema
	// contributes to its inferred fields.
	sqlCatalogSample = 20
	// ponytail: fields load for the first 20 collections only, one sample
	// query each; load the rest on demand if databases outgrow it.
	sqlCatalogFieldCollections = 20
	// maxFieldDepth matches the parser's limit of 8 members per field path.
	maxFieldDepth = 8
)

// sqlCatalog is what the SQL editor completes and explains: the database's
// collections and their fields, and the dialect's keywords.
type sqlCatalog struct {
	Keywords    []string               `json:"keywords"`
	Collections []sqlCatalogCollection `json:"collections"`
}

type sqlCatalogCollection struct {
	Name string `json:"name"`
	// Documents counts live documents; it is a lower bound when Approximate.
	Documents   uint64 `json:"documents"`
	Approximate bool   `json:"approximate,omitempty"`
	// Source is "schema" when the fields come from the collection schema and
	// "sample" when they are inferred from recent documents.
	Source string            `json:"source"`
	Fields []sqlCatalogField `json:"fields"`
}

type sqlCatalogField struct {
	Path string `json:"path"`
	Type string `json:"type"`
}

// SQLCatalog lists the collections of a database with their document counts
// and fields: the declared schema fields, or the fields of a sample of recent
// documents when the collection has no schema.
func (s Service) SQLCatalog(ctx context.Context, p core.Principal, databaseID string) (sqlCatalog, error) {
	catalog := sqlCatalog{Keywords: sqlKeywords, Collections: []sqlCatalogCollection{}}
	db, err := s.database(ctx, p, databaseID)
	if err != nil {
		return catalog, err
	}
	names, err := s.Collections(ctx, p, databaseID)
	if err != nil {
		return catalog, err
	}
	catalog.Collections = make([]sqlCatalogCollection, len(names))
	if s.deps.LocalResources() {
		for i, name := range names {
			documents := slices.Sorted(maps.Keys(db.Documents[name]))
			sample := make([][]byte, 0, sqlCatalogSample)
			for _, id := range documents[:min(len(documents), sqlCatalogSample)] {
				sample = append(sample, db.Documents[name][id])
			}
			catalog.Collections[i] = catalogCollection(name, uint64(len(documents)), false, db.Schemas[name], sample)
		}
		return catalog, nil
	}
	// TerraBase's system databases complete by collection name only.
	if core.SystemDatabase(db) {
		for i, name := range names {
			catalog.Collections[i] = catalogCollection(name, 0, true, nil, nil)
		}
		return catalog, nil
	}
	client, err := s.deps.Documents(p, db.Name)
	if err != nil {
		return catalog, err
	}
	var wg sync.WaitGroup
	for i, name := range names {
		catalog.Collections[i] = catalogCollection(name, 0, true, nil, nil)
		if i >= sqlCatalogFieldCollections {
			continue
		}
		wg.Go(func() {
			// A collection that fails to load still completes by name.
			schema := db.Schemas[name]
			page, err := client.Collection(name).Query(ctx, v1.Query{Limit: sqlCatalogSample, Count: true})
			if err != nil {
				catalog.Collections[i] = catalogCollection(name, 0, true, schema, nil)
				return
			}
			sample := make([][]byte, 0, len(page.Items))
			for _, item := range page.Items {
				sample = append(sample, item.Document)
			}
			var count uint64
			approximate := page.Count == nil
			if page.Count != nil {
				count, approximate = uint64(page.Count.Value), !page.Count.Exact
			}
			catalog.Collections[i] = catalogCollection(name, count, approximate, schema, sample)
		})
	}
	wg.Wait()
	return catalog, nil
}

// catalogCollection describes one collection from its schema JSON, if any,
// or else from sample documents.
func catalogCollection(name string, documents uint64, approximate bool, schemaJSON []byte, sample [][]byte) sqlCatalogCollection {
	collection := sqlCatalogCollection{Name: name, Documents: documents, Approximate: approximate, Source: "sample"}
	types := map[string]string{"_id": "string", "_rev": "string"}
	var schema sdk.CollectionSchema
	if len(schemaJSON) > 0 && json.Unmarshal(schemaJSON, &schema) == nil && len(schema.Fields) > 0 {
		collection.Source = "schema"
		for _, field := range schema.Fields {
			types[field.Name] = string(field.Type)
		}
	} else {
		for _, document := range sample {
			inferFields(jsontext.Value(document), "", 0, types)
		}
	}
	for _, path := range slices.Sorted(maps.Keys(types)) {
		collection.Fields = append(collection.Fields, sqlCatalogField{Path: path, Type: types[path]})
	}
	return collection
}

// inferFields records the path and JSON type of every member of a document,
// nested objects included. A path seen with different types is "mixed".
func inferFields(value jsontext.Value, prefix string, depth int, types map[string]string) {
	var members map[string]jsontext.Value
	if depth >= maxFieldDepth || value.Kind() != '{' || json.Unmarshal(value, &members) != nil {
		return
	}
	for name, member := range members {
		if !sqlMemberPattern.MatchString(name) {
			continue
		}
		path := prefix + name
		kind := jsonKind(member.Kind())
		if seen, ok := types[path]; ok && seen != kind && seen != "null" {
			if kind != "null" {
				types[path] = "mixed"
			}
		} else {
			types[path] = kind
		}
		inferFields(member, path+".", depth+1, types)
	}
}

func jsonKind(kind jsontext.Kind) string {
	switch kind {
	case '"':
		return "string"
	case '0':
		return "number"
	case 't', 'f':
		return "boolean"
	case '{':
		return "object"
	case '[':
		return "array"
	default:
		return "null"
	}
}

const sqlParameterError = "The console cannot bind parameters such as $1; replace them with values."

// sqlDiagnostic is a problem in a statement, located by UTF-16 offsets as
// the editor counts them.
type sqlDiagnostic struct {
	Message  string `json:"message"`
	Severity string `json:"severity"`
	From     int    `json:"from"`
	To       int    `json:"to"`
}

// sqlDiagnostics explains why the dashboard refuses a statement, and which
// parts of an accepted one TerraBase's dialect does not support.
func sqlDiagnostics(statement string) []sqlDiagnostic {
	diagnostics := []sqlDiagnostic{}
	if strings.TrimSpace(statement) == "" {
		return diagnostics
	}
	at := func(severity, message string, from, to int) {
		diagnostics = append(diagnostics, sqlDiagnostic{Message: message, Severity: severity, From: utf16Offset(statement, from), To: utf16Offset(statement, to)})
	}
	visible, ok := visibleSQL(statement)
	words := sqlWordPattern.FindAllStringIndex(visible, -1)
	if analysis := analyzeSQL(statement); !analysis.OK {
		from, to := len(statement)-len(strings.TrimLeft(statement, " \t\r\n")), len(strings.TrimRight(statement, " \t\r\n"))
		if i := slices.IndexFunc(words, func(w []int) bool { return sqlForbiddenWords[strings.ToUpper(visible[w[0]:w[1]])] }); ok && i >= 0 {
			from, to = words[i][0], words[i][1]
		}
		at("error", analysis.Error, from, to)
		return diagnostics
	}
	for i, w := range words {
		// Members of field paths and parts of collection names are names,
		// even when they read as keywords: customer.offset, FROM union-items.
		joined := func(at int) bool { return at >= 0 && at < len(visible) && (visible[at] == '.' || visible[at] == '-') }
		if joined(w[0]-1) || joined(w[1]) || i > 0 && strings.EqualFold(visible[words[i-1][0]:words[i-1][1]], "FROM") || fieldName(visible, words, i) {
			continue
		}
		word := strings.ToUpper(visible[w[0]:w[1]])
		rest := strings.TrimLeft(visible[w[1]:], " \t\r\n")
		switch {
		case sqlUnsupported[word] != "":
			at("warning", "TerraBase: "+sqlUnsupported[word], w[0], w[1])
		case strings.HasPrefix(rest, "(") && word != "IN" && !slices.Contains(sqlKeywords, word):
			at("warning", "TerraBase: functions are not supported.", w[0], w[1])
		case word == "LIMIT":
			from := len(visible) - len(rest)
			to := from + strings.IndexAny(rest+" ", " \t\r\n;)")
			value := visible[from:to]
			if n, err := strconv.Atoi(value); err != nil || strings.TrimLeft(value, "0123456789") != "" || n < 1 || n > 1000 {
				if from == to {
					from, to = w[0], w[1]
				}
				at("warning", "TerraBase: LIMIT must be an integer from 1 to 1000.", from, to)
			}
		}
	}
	// analyzeSQL accepts $1 for checking; running and saving bind nothing.
	if sdk.ValidateSQL(statement, nil) != nil {
		from, to := 0, len(statement)
		if loc := sqlParameterPattern.FindStringIndex(visible); loc != nil {
			from, to = loc[0], loc[1]
		}
		at("error", sqlParameterError, from, to)
	}
	return diagnostics
}

// sqlFieldStarts may come right before a field and sqlFieldEnds right after
// one.
var (
	sqlFieldStarts = []string{"SELECT", "WHERE", "AND", "OR", "NOT", "BY"}
	sqlFieldEnds   = []string{"FROM", "IS", "IN", "NOT", "AND", "OR", "ASC", "DESC", "ORDER", "LIMIT"}
)

// fieldName reports whether words[i] stands where a field does, so a field
// named offset or limit reads as a name: after SELECT, WHERE, AND, OR, NOT,
// BY, a comma or a parenthesis, and before FROM, a comparison, a comma, a
// clause keyword or the end.
func fieldName(visible string, words [][]int, i int) bool {
	upper := func(j int) string { return strings.ToUpper(visible[words[j][0]:words[j][1]]) }
	w := words[i]
	if i == 0 {
		return false
	}
	if before := strings.TrimSpace(visible[words[i-1][1]:w[0]]); before == "" {
		if !slices.Contains(sqlFieldStarts, upper(i-1)) {
			return false
		}
	} else if !strings.HasSuffix(before, ",") && !strings.HasSuffix(before, "(") {
		return false
	}
	end := len(visible)
	if i+1 < len(words) {
		end = words[i+1][0]
	}
	if after := strings.TrimSpace(visible[w[1]:end]); after != "" {
		return strings.ContainsRune(",);=!<>", rune(after[0]))
	}
	return i+1 == len(words) || slices.Contains(sqlFieldEnds, upper(i+1))
}

// utf16Offset converts a byte offset in s to UTF-16 code units.
func utf16Offset(s string, offset int) int {
	n := 0
	for _, r := range s[:offset] {
		n++
		if r > 0xFFFF {
			n++
		}
	}
	return n
}
