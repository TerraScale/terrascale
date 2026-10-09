package data

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestDemoDocumentKeyOwnsIdentity(t *testing.T) {
	id := core.NewID()
	db := core.Database{}
	body := []byte(fmt.Sprintf(`{"id":%q,"amount":123456789012345678901234567890,"nested":{"id":%q},"html":"<>&"}`, id, id))
	for _, operation := range []string{"create", "replace", "patch"} {
		if err := writeDemoDocument(&db, "items", operation, id, body); err != nil {
			t.Fatal(err)
		}
		stored := db.Documents["items"][id]
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(stored, &fields); err != nil {
			t.Fatal(err)
		}
		if _, repeats := fields["id"]; repeats {
			t.Fatalf("%s repeated the document key", operation)
		}
		if string(fields["amount"]) != "123456789012345678901234567890" || !bytes.Contains(fields["nested"], []byte(id)) || string(fields["html"]) != `"<>&"` {
			t.Fatalf("payload data changed: %s", stored)
		}
		versions := db.DocumentHistory["items"][id]
		if !bytes.Equal(versions[len(versions)-1], stored) {
			t.Fatal("history repeated the removed identity")
		}
	}
	body = []byte(`{"id":"business-reference"}`)
	if err := writeDemoDocument(&db, "items", "replace", id, body); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(db.Documents["items"][id], body) {
		t.Fatal("unrelated payload id was changed")
	}
}

func TestDemoDocumentIdentityPreservesRawJSON(t *testing.T) {
	const id = "01JC9Q8A2C4E6G8J0M2N4Q7STV"
	cases := []struct{ name, body, want string }{
		{"duplicate fields", `{"a":1,"a":2}`, `{"a":1,"a":2}`},
		{"identity first", `{"id":"` + id + `","a":1,"a":2}`, `{"a":1,"a":2}`},
		{"identity middle", `{"a":1,"id":"` + id + `","a":2}`, `{"a":1,"a":2}`},
		{"identity last", `{"a":1,"a":2,"id":"` + id + `"}`, `{"a":1,"a":2}`},
		{"identity only", `{"id":"` + id + `"}`, `{}`},
		{"escaped names and nested duplicates", `{"\u0069d":"` + id + `","\u0061":{"id":"nested","x":1,"x":2},"n":1e+100}`, `{"\u0061":{"id":"nested","x":1,"x":2},"n":1e+100}`},
		{"duplicate matching identity", `{"id":"` + id + `","a":1,"id":"` + id + `"}`, `{"a":1}`},
		{"last identity matches", `{"id":"business","id":"` + id + `"}`, `{"id":"business"}`},
		{"last identity differs", `{"id":"` + id + `","id":"business"}`, `{"id":"` + id + `","id":"business"}`},
	}
	large := `{"id":"` + id + `","raw":"` + strings.Repeat("<", maxDocumentBytes-len(`{"id":"`+id+`","raw":""}`)) + `"}`
	cases = append(cases, struct{ name, body, want string }{"1 MiB payload", large, strings.Replace(large, `"id":"`+id+`",`, "", 1)})
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			db := core.Database{}
			for _, operation := range []string{"create", "replace"} {
				if err := writeDemoDocument(&db, "items", operation, id, []byte(tc.body)); err != nil {
					t.Fatal(err)
				}
				if stored := db.Documents["items"][id]; !bytes.Equal(stored, []byte(tc.want)) {
					t.Fatalf("%s changed application tokens: stored %d bytes, want %d", operation, len(stored), len(tc.want))
				}
			}
		})
	}
	for _, body := range []string{`{"id":`, `{"x":1,}`, "{\"x\":\"\xff\"}"} {
		if _, err := omitDocumentIdentity([]byte(body), id); !errors.Is(err, core.ErrInvalid) {
			t.Fatalf("malformed payload must be a client error: %v", err)
		}
	}
}

func TestRawDocumentKeysRemainDistinctInDemo(t *testing.T) {
	ctx := t.Context()
	store := memstore.New()
	account, err := store.CreateAccount(ctx, core.User{Name: "Owner", Email: "raw@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	db := core.Database{ID: core.NewID(), ProjectID: p.ProjectID, Name: "raw", Status: "ready"}
	if err := store.UpdateAs(ctx, p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		if project.Databases == nil {
			project.Databases = make(map[string]core.Database)
		}
		project.Databases[db.ID] = db
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	service := Service{deps: core.Deps{Store: store, Demo: true}}
	keys := []string{"a", " a ", " ", "a/b", "a%2Fb", ".", "%2E", "..", "%2E%2E", "%", "?", "#", "+", "界😀", strings.Repeat("界", 256), strings.Repeat("😀", 128)}
	for i, key := range keys {
		body := []byte(fmt.Sprintf(`{"marker":%d}`, i))
		written, err := service.WriteDocument(ctx, p, db.ID, "items", "create", key, body)
		if err != nil || written.ID != key {
			t.Fatalf("create %q: %+v, %v", key, written, err)
		}
		if _, err := service.WriteDocument(ctx, p, db.ID, "items", "patch", key, []byte(fmt.Sprintf(`{"patched":%d}`, i))); err != nil {
			t.Fatal(err)
		}
	}
	for i, key := range keys {
		doc, err := service.Document(ctx, p, db.ID, "items", key)
		if err != nil {
			t.Fatal(err)
		}
		var fields map[string]int
		if err := json.Unmarshal(doc.Value, &fields); err != nil || fields["marker"] != i || fields["patched"] != i {
			t.Fatalf("key %q mutated another document: %s, %v", key, doc.Value, err)
		}
		if _, err := service.WriteDocument(ctx, p, db.ID, "items", "delete", key, nil); err != nil {
			t.Fatal(err)
		}
	}
	for _, key := range []string{"bad\x00key", "bad\nkey", "\xff", strings.Repeat("a", 257), strings.Repeat("😀", 129)} {
		if _, err := service.WriteDocument(ctx, p, db.ID, "items", "create", key, []byte(`{}`)); !errors.Is(err, core.ErrInvalid) {
			t.Fatalf("invalid key %q: %v", key, err)
		}
	}
	if _, err := service.WriteDocument(ctx, p, db.ID, "items", "create", "duplicates", []byte(`{"a":1,"a":2}`)); !errors.Is(err, core.ErrInvalid) {
		t.Fatalf("duplicate-member input must be a client error: %v", err)
	} else if status, _ := documentFailure(err, "duplicates", "items"); status != http.StatusBadRequest {
		t.Fatalf("duplicate-member input was blamed on the data plane: HTTP %d", status)
	}
}
