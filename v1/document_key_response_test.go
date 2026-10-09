package v1_test

import (
	"encoding/json"
	"net/http"
	"strings"
	"testing"

	"terrascale.tech/sdk/v1"
)

func TestDocumentKeyResponseValidation(t *testing.T) {
	cases := []struct {
		name, key string
		omit      bool
		valid     bool
	}{
		{name: "missing", omit: true},
		{name: "empty"},
		{name: "NUL", key: "a\x00b"},
		{name: "newline", key: "a\nb"},
		{name: "C1 control", key: "a\u0085b"},
		{name: "too long ASCII", key: strings.Repeat("a", 257)},
		{name: "too long BMP", key: strings.Repeat("界", 257)},
		{name: "too long astral", key: strings.Repeat("😀", 129)},
		{name: "raw punctuation", key: " /a%2Fb?#+ ", valid: true},
		{name: "decomposed Unicode", key: "e\u0301", valid: true},
		{name: "BMP boundary", key: strings.Repeat("界", 256), valid: true},
		{name: "astral boundary", key: strings.Repeat("😀", 128), valid: true},
		{name: "ULID", key: testProject, valid: true},
	}
	for _, tc := range cases {
		for _, operation := range []string{"get", "list", "query", "sql", "put", "patch", "delete", "tx_create", "tx_put", "tx_patch", "tx_delete"} {
			t.Run(tc.name+"/"+operation, func(t *testing.T) {
				payload := map[string]any{"id": "application\x00data"}
				row := map[string]any{"version": "opaque", "state": "value", "document": payload, "value": payload}
				if !tc.omit {
					row["id"] = tc.key
				}
				var response any = row
				switch operation {
				case "list", "query":
					response = map[string]any{"items": []any{row}}
				case "sql":
					response = map[string]any{"rows": []any{row}}
				case "tx_create", "tx_put", "tx_patch", "tx_delete":
					response = map[string]any{"results": []any{row}}
				}
				body, err := json.Marshal(response)
				if err != nil {
					t.Fatal(err)
				}
				users, db, transport := scriptedUsers(t, scripted{status: http.StatusOK, body: string(body)})
				switch operation {
				case "get":
					_, err = users.Get(t.Context(), "request-key")
				case "list":
					_, err = users.List(t.Context(), v1.ListOptions{})
				case "query":
					_, err = users.Query(t.Context(), v1.Query{})
				case "sql":
					m, initErr := v1.NewManagement(v1.Config{BaseURL: "https://tb.example", HTTPClient: &http.Client{Transport: transport}})
					if initErr != nil {
						t.Fatal(initErr)
					}
					_, err = m.WithToken("test-access-token").SQL(t.Context(), testOrg, testProject, testProject, "SELECT * FROM users")
				case "put":
					_, err = users.Put(t.Context(), "request-key", map[string]any{})
				case "patch":
					_, err = users.Patch(t.Context(), "request-key", v1.MergePatch(map[string]any{}))
				case "delete":
					_, err = users.Delete(t.Context(), "request-key")
				case "tx_create":
					_, err = db.Tx().Create("users", "request-key", map[string]any{}).Commit(t.Context())
				case "tx_put":
					_, err = db.Tx().Put("users", "request-key", map[string]any{}).Commit(t.Context())
				case "tx_patch":
					_, err = db.Tx().Patch("users", "request-key", v1.MergePatch(map[string]any{})).Commit(t.Context())
				case "tx_delete":
					_, err = db.Tx().Delete("users", "request-key").Commit(t.Context())
				}
				if (err == nil) != tc.valid {
					t.Fatalf("valid = %v, error = %v", tc.valid, err)
				}
			})
		}
	}
}

func TestTransactionResponseKeysAreOptionalOnlyForAssertions(t *testing.T) {
	_, db, _ := scriptedUsers(t, scripted{status: http.StatusOK, body: `{"results":[{"asserted":true},{"asserted":true},{"count":"0"}]}`})
	zero := uint64(0)
	_, err := db.Tx().Assert("users", "present", v1.Eq("/active", true)).AssertAbsent("users", "absent").AssertCount("users", v1.Eq("/active", true), v1.CountBounds{Gte: &zero}).Commit(t.Context())
	if err != nil {
		t.Fatalf("assertion results may omit IDs: %v", err)
	}
	_, db, _ = scriptedUsers(t, scripted{status: http.StatusOK, body: `{"results":[]}`})
	if _, err := db.Tx().Put("users", "raw-key", map[string]any{}).Commit(t.Context()); err == nil {
		t.Fatal("accepted a missing write result")
	}
}
