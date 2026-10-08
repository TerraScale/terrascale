package terrabase

import (
	"encoding/json"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestEntityKeysCarryIdentityAndPreserveReferences(t *testing.T) {
	account, err := core.NewAccount(core.User{Name: "Key owner", Email: "keys@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	org := account.Organizations[account.ActiveOrganizationID]
	state := memstore.State{Accounts: map[string]core.Account{account.ID: account}, Organizations: map[string]core.Organization{org.ID: org}}
	records, err := encodeCore(state, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	for key, record := range records {
		if !core.ValidID(key.id) {
			t.Fatalf("%s has non-ULID key %q", key.collection, key.id)
		}
		body, err := json.Marshal(record)
		if err != nil {
			t.Fatal(err)
		}
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(body, &fields); err != nil {
			t.Fatal(err)
		}
		for name := range fields {
			if strings.EqualFold(name, "id") {
				t.Fatalf("%s repeats its document key in payload", key.collection)
			}
		}
		if len(record.Data) > 0 {
			if err := json.Unmarshal(record.Data, &fields); err != nil {
				t.Fatal(err)
			}
			for name := range fields {
				if strings.EqualFold(name, "id") {
					t.Fatalf("%s repeats its identity in domain data", key.collection)
				}
			}
		}
	}
	if recordKey("users", account.ID).id != account.ID {
		t.Fatal("account was assigned a second storage ID")
	}
	got, err := decodeData[core.Account](records[recordKey("users", account.ID)])
	if err != nil || got.ID != account.ID || got.User.ID != account.User.ID || got.ActiveOrganizationID != org.ID {
		t.Fatalf("account identity/references changed: %+v, %v", got, err)
	}
	project, err := decodeData[core.Project](records[recordKey("projects", account.ActiveProjectID)])
	if err != nil || project.ID != account.ActiveProjectID || project.OrganizationID != org.ID {
		t.Fatalf("project identity/references changed: %+v, %v", project, err)
	}
}
