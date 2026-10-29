package v1_test

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"terrascale.tech/sdk/v1"
)

func TestTransactionQuotesOpaqueVersionsAndOmitsZeroFilter(t *testing.T) {
	peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Ops []map[string]json.RawMessage `json:"ops"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		var version string
		if err := json.Unmarshal(body.Ops[0]["if_match"], &version); err != nil {
			t.Error(err)
		}
		if version != `"opaque-version"` {
			t.Errorf("if_match = %q", version)
		}
		if _, present := body.Ops[1]["filter"]; present {
			t.Error("zero filter must be omitted")
		}
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte(`{"offset":"1","durability":"local_durable","results":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD3","version":"opaque","document":{"id":"application"}},{"asserted":true,"count":"0"}]}`))
	}))
	t.Cleanup(peer.Close)
	c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	zero := uint64(0)
	result, err := c.Database(testProject, "main").Tx().Patch("users", "01ARZ3NDEKTSV4RRFFQ69G5FD3", v1.MergePatch(map[string]any{"id": "application"}), v1.IfMatch("opaque-version")).AssertCount("users", v1.Filter{}, v1.CountBounds{Lte: &zero}).Commit(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	if string(result.Results[0].Document) != `{"id":"application"}` {
		t.Fatalf("missing stored patch document: %s", result.Results[0].Document)
	}
}

func TestDatabaseCreateUsesPublicPOST(t *testing.T) {
	_, db, transport := scriptedUsers(t, scripted{status: http.StatusCreated, body: `{"id":"` + testProject + `","projectId":"` + testProject + `","name":"main"}`})
	result, err := db.Create(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	request := transport.requests[0]
	if request.Method != http.MethodPost || request.URL.Path != "/v1/projects/"+testProject+"/databases" || !result.Created {
		t.Fatalf("database create: %s %s, created=%v", request.Method, request.URL.Path, result.Created)
	}
}

func TestTransactionRejectsMissingAndExtraAssertionResults(t *testing.T) {
	for _, body := range []string{`{"offset":"1","durability":"local_durable","results":[]}`, `{"offset":"1","durability":"local_durable","results":[{"asserted":true},{"asserted":true}]}`} {
		t.Run(body, func(t *testing.T) {
			_, db, _ := scriptedUsers(t, scripted{status: http.StatusOK, body: body})
			if _, err := db.Tx().AssertAbsent("users", "01ARZ3NDEKTSV4RRFFQ69G5FD3").Commit(t.Context()); err == nil {
				t.Fatal("accepted mismatched transaction results")
			}
		})
	}
}

func TestRequiredDocumentResponseMembers(t *testing.T) {
	for _, operation := range []string{"get", "put", "tx", "collection"} {
		fields := []string{"id", "version", "state", "document"}
		switch operation {
		case "put":
			fields = []string{"id", "version", "offset", "durability"}
		case "tx":
			fields = []string{"offset", "durability", "results"}
		case "collection":
			fields = []string{"id"}
		}
		for _, field := range fields {
			for _, null := range []bool{false, true} {
				t.Run(operation+"/"+field+fmt.Sprint("/null=", null), func(t *testing.T) {
					body := map[string]any{"id": testProject, "version": "opaque", "state": "value", "document": map[string]any{}, "offset": "1", "durability": "local_durable", "results": []any{map[string]any{"id": "01ARZ3NDEKTSV4RRFFQ69G5FD3", "version": "opaque"}}, "name": "users", "created_at": "1"}
					if null {
						body[field] = nil
					} else {
						delete(body, field)
					}
					raw, err := json.Marshal(body)
					if err != nil {
						t.Fatal(err)
					}
					users, db, _ := scriptedUsers(t, scripted{status: http.StatusOK, body: string(raw)})
					switch operation {
					case "get":
						_, err = users.Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD3")
					case "put":
						_, err = users.Put(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD3", map[string]any{})
					case "tx":
						_, err = db.Tx().Put("users", "01ARZ3NDEKTSV4RRFFQ69G5FD3", map[string]any{}).Commit(t.Context())
					case "collection":
						_, err = users.Info(t.Context())
					}
					if err == nil {
						t.Fatal("accepted missing or null required response member")
					}
				})
			}
		}
	}
}

func TestCollectionResponsesExposeCanonicalIDs(t *testing.T) {
	const collection = `{"id":"` + testProject + `","name":"users","created_at":"1"}`
	users, db, _ := scriptedUsers(t,
		scripted{status: http.StatusCreated, body: collection},
		scripted{status: http.StatusOK, body: collection},
		scripted{status: http.StatusOK, body: `{"items":[` + collection + `],"next_cursor":null}`},
		scripted{status: http.StatusOK, body: collection})
	created, err := db.CreateCollection(t.Context(), "users")
	if err != nil || created.ID != testProject {
		t.Fatalf("create collection = %+v, %v", created, err)
	}
	info, err := users.Info(t.Context())
	if err != nil || info.ID != testProject {
		t.Fatalf("collection info = %+v, %v", info, err)
	}
	page, err := db.Collections(t.Context(), v1.ListOptions{})
	if err != nil || len(page.Items) != 1 || page.Items[0].ID != testProject {
		t.Fatalf("collection page = %+v, %v", page, err)
	}
	dropped, err := users.Drop(t.Context())
	if err != nil || dropped.ID != testProject {
		t.Fatalf("drop collection = %+v, %v", dropped, err)
	}
}

func TestPatchResponsesRequireStoredObject(t *testing.T) {
	for _, raw := range []string{"", "null", "[]", `"text"`, "1", "true", " {} "} {
		for _, transaction := range []bool{false, true} {
			t.Run(fmt.Sprintf("%q/transaction=%v", raw, transaction), func(t *testing.T) {
				row := `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD3","version":"opaque"`
				if raw != "" {
					row += `,"document":` + raw
				}
				var body string
				if transaction {
					body = `{"offset":"1","durability":"local_durable","results":[` + row + `}]}`
				} else {
					body = row + `,"offset":"1","durability":"local_durable"}`
				}
				users, db, transport := scriptedUsers(t, scripted{status: http.StatusOK, body: body})
				var err error
				if transaction {
					_, err = db.Tx().Patch("users", "01ARZ3NDEKTSV4RRFFQ69G5FD3", v1.MergePatch(map[string]any{}), v1.IfMatch("opaque")).Commit(t.Context())
				} else {
					_, err = users.Patch(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD3", v1.MergePatch(map[string]any{}), v1.IfMatch("opaque"))
				}
				if (err == nil) != (raw == " {} ") {
					t.Fatalf("stored document %q: %v", raw, err)
				}
				if len(transport.requests) != 1 {
					t.Fatalf("malformed acknowledgement replayed %d times", len(transport.requests))
				}
			})
		}
	}
}
