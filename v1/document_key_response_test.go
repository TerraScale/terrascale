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
		stored    bool
	}{
		{name: "missing", omit: true},
		{name: "empty"},
		{stored: true, name: "UUID", key: "0190a6e4-7c3b-7d2e-8f00-0123456789ab"},
		{stored: true, name: "prefix", key: "doc_" + testProject},
		{stored: true, name: "lowercase", key: strings.ToLower(testProject)},
		{stored: true, name: "overflow", key: "81ARZ3NDEKTSV4RRFFQ69G5FAV"},
		{name: "NUL", key: "a\x00b"},
		{name: "newline", key: "a\nb"},
		{name: "C1 control", key: "a\u0085b"},
		{name: "too long ASCII", key: strings.Repeat("a", 257)},
		{name: "too long BMP", key: strings.Repeat("界", 257)},
		{name: "too long astral", key: strings.Repeat("😀", 129)},
		{stored: true, name: "raw punctuation", key: " /a%2Fb?#+ "},
		{stored: true, name: "decomposed Unicode", key: "e\u0301"},
		{stored: true, name: "BMP boundary", key: strings.Repeat("界", 256)},
		{stored: true, name: "astral boundary", key: strings.Repeat("😀", 128)},
		{name: "ULID", key: testProject, valid: true},
	}
	for _, tc := range cases {
		for _, operation := range []string{"get", "list", "query", "sql", "put", "patch", "delete", "tx_create", "tx_put", "tx_patch", "tx_delete"} {
			t.Run(tc.name+"/"+operation, func(t *testing.T) {
				payload := map[string]any{"name": "Ana"}
				row := map[string]any{"version": "opaque", "state": "value", "document": payload, "value": payload, "offset": "1", "durability": "local_durable"}
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
					response = map[string]any{"results": []any{row}, "offset": "1", "durability": "local_durable"}
				}
				body, err := json.Marshal(response)
				if err != nil {
					t.Fatal(err)
				}
				users, db, transport := scriptedUsers(t, scripted{status: http.StatusOK, body: string(body)})
				switch operation {
				case "get":
					_, err = users.Get(t.Context(), testProject)
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
					_, err = users.Put(t.Context(), testProject, map[string]any{})
				case "patch":
					_, err = users.Patch(t.Context(), testProject, v1.MergePatch(map[string]any{}))
				case "delete":
					_, err = users.Delete(t.Context(), testProject)
				case "tx_create":
					_, err = db.Tx().Create("users", testProject, map[string]any{}).Commit(t.Context())
				case "tx_put":
					_, err = db.Tx().Put("users", testProject, map[string]any{}).Commit(t.Context())
				case "tx_patch":
					_, err = db.Tx().Patch("users", testProject, v1.MergePatch(map[string]any{})).Commit(t.Context())
				case "tx_delete":
					_, err = db.Tx().Delete("users", testProject).Commit(t.Context())
				}
				valid := tc.valid
				if operation == "list" || operation == "query" || operation == "sql" {
					valid = valid || tc.stored
				}
				if (err == nil) != valid {
					t.Fatalf("valid = %v, error = %v", valid, err)
				}
			})
		}
	}
}

func TestTransactionResponseKeysAreOptionalOnlyForAssertions(t *testing.T) {
	_, db, _ := scriptedUsers(t, scripted{status: http.StatusOK, body: `{"offset":"1","durability":"local_durable","results":[{"asserted":true},{"asserted":true},{"asserted":true,"count":"0"}]}`})
	zero := uint64(0)
	_, err := db.Tx().Assert("users", testProject, v1.Eq("/active", true)).AssertAbsent("users", v1.NewID()).AssertCount("users", v1.Eq("/active", true), v1.CountBounds{Gte: &zero}).Commit(t.Context())
	if err != nil {
		t.Fatalf("assertion results may omit IDs: %v", err)
	}
	_, db, _ = scriptedUsers(t, scripted{status: http.StatusOK, body: `{"results":[]}`})
	if _, err := db.Tx().Put("users", testProject, map[string]any{}).Commit(t.Context()); err == nil {
		t.Fatal("accepted a missing write result")
	}
}
