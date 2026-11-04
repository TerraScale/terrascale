package v1_test

import (
	"errors"
	"net/http"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestSQLSuccessEnvelopeValidation(t *testing.T) {
	valid := `{"columns":[{"name":"id","decltype":null}],"rows":[[1]],"rows_affected":0,"last_insert_rowid":null}`
	for name, body := range map[string]string{
		"null": `null`, "missing results": `{}`, "null results": `{"results":null}`,
		"empty result": `{"results":[{}]}`, "null result": `{"results":[null]}`,
		"too few results": `{"results":[]}`, "truncated": `{"results":[`,
		"null columns":      strings.Replace(valid, `[{"name":"id","decltype":null}]`, `null`, 1),
		"null rows":         strings.Replace(valid, `[[1]]`, `null`, 1),
		"null row":          strings.Replace(valid, `[[1]]`, `[null]`, 1),
		"short row":         strings.Replace(valid, `[[1]]`, `[[]]`, 1),
		"wide row":          strings.Replace(valid, `[[1]]`, `[[1,2]]`, 1),
		"object cell":       strings.Replace(valid, `[[1]]`, `[[{}]]`, 1),
		"array cell":        strings.Replace(valid, `[[1]]`, `[[[]]]`, 1),
		"boolean cell":      strings.Replace(valid, `[[1]]`, `[[true]]`, 1),
		"overflow cell":     strings.Replace(valid, `[[1]]`, `[[1e999]]`, 1),
		"negative affected": strings.Replace(valid, `"rows_affected":0`, `"rows_affected":-1`, 1),
		"null affected":     strings.Replace(valid, `"rows_affected":0`, `"rows_affected":null`, 1),
		"overflow affected": strings.Replace(valid, `"rows_affected":0`, `"rows_affected":9223372036854775808`, 1),
		"missing insert ID": strings.Replace(valid, `,"last_insert_rowid":null`, ``, 1),
		"missing name":      strings.Replace(valid, `"name":"id",`, ``, 1),
		"null name":         strings.Replace(valid, `"name":"id"`, `"name":null`, 1),
		"missing decltype":  strings.Replace(valid, `,"decltype":null`, ``, 1),
	} {
		t.Run(name, func(t *testing.T) {
			if strings.Contains(body, `"columns"`) {
				body = `{"results":[` + body + `]}`
			}
			db, calls := sqlServer(t, http.StatusOK, body, nil)
			_, err := db.ExecuteSQL(t.Context(), "INSERT INTO t VALUES (1)")
			var protocol *tb.SQLProtocolError
			if !errors.As(err, &protocol) || !errors.Is(err, tb.ErrCommitUncertain) || *calls != 1 {
				t.Fatalf("error=%v calls=%d", err, *calls)
			}
		})
	}
}
