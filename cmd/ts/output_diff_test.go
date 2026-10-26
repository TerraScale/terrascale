package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestJSONMigrationDiffUsesTypedMetadata(t *testing.T) {
	t.Parallel()
	for _, changed := range []bool{false, true} {
		s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "application/json")
			switch {
			case strings.HasSuffix(r.URL.Path, "/collections"):
				io.WriteString(w, `{"items":[{"id":"`+jsonAuthID+`","name":"users","created_at":"1"}],"next_cursor":null}`)
			case strings.HasSuffix(r.URL.Path, "/indexes"):
				io.WriteString(w, `{"items":[]}`)
			default:
				columns := []map[string]string{{"name": "id", "decltype": "INTEGER"}}
				if changed && strings.Contains(r.URL.Path, "/branch/") {
					columns = append(columns, map[string]string{"name": "email", "decltype": "TEXT"})
				}
				json.NewEncoder(w).Encode(map[string]any{"results": []any{map[string]any{"columns": columns, "rows": []any{}, "rows_affected": 0}}})
			}
		})
		var buf bytes.Buffer
		if err := s.migrateDiff(t.Context(), "branch", "main", &jsonOutput{Writer: &buf}); err != nil {
			t.Fatal(err)
		}
		var result struct {
			Base, Branch string
			Changes      []schemaChange
		}
		if err := json.Unmarshal(buf.Bytes(), &result); err != nil || result.Base != "main" || result.Branch != "branch" || result.Changes == nil {
			t.Fatal("invalid structured diff")
		}
		if changed {
			if len(result.Changes) != 1 || result.Changes[0].Kind != "table" || len(result.Changes[0].Before.Columns) != 1 || len(result.Changes[0].After.Columns) != 2 {
				t.Fatal("typed column change missing")
			}
		} else if len(result.Changes) != 0 {
			t.Fatal("unchanged schema must have [] changes")
		}
	}
}
