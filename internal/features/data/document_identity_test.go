package data

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
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
}
