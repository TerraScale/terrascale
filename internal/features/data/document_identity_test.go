package data

import (
	"bytes"
	"encoding/json"
	"fmt"
	"testing"

	"terrascale.tech/dashboard/internal/core"
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
