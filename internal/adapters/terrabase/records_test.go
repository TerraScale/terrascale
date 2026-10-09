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
	token, _, err := core.NewAccessToken("fresh token", time.Hour, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	account.AccessTokens = []core.AccessToken{token}
	project := org.Projects[account.ActiveProjectID]
	databaseID, backupID := core.NewID(), core.NewID()
	backupDocument := []byte(`{"id":"application-data","total":42}`)
	project.Databases[databaseID] = core.Database{
		ID: databaseID, ProjectID: project.ID,
		Backups: []core.Backup{{ID: backupID, DatabaseID: databaseID, Documents: map[string]map[string][]byte{"orders": {"order": backupDocument}}}},
	}
	org.Projects[project.ID] = project
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
		if len(record.Data) > 0 && record.Data[0] == '{' {
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
	loadedProject, err := decodeData[core.Project](records[recordKey("projects", account.ActiveProjectID)])
	if err != nil || loadedProject.ID != account.ActiveProjectID || loadedProject.OrganizationID != org.ID {
		t.Fatalf("project identity/references changed: %+v, %v", loadedProject, err)
	}
	tokenClaim := records[claimKey("access_tokens", "token_hash", token.SecretHash)]
	loadedToken, err := decodeData[core.AccessToken](records[recordKey("access_tokens", tokenClaim.OwnerID)])
	if err != nil || tokenClaim.OwnerID != token.ID || loadedToken.ID != token.ID || loadedToken.SecretHash != token.SecretHash {
		t.Fatal("fresh token claim did not resolve its ULID-addressed token", err)
	}
	var databaseRecords []coreRecord
	for key, record := range records {
		if key.collection == "databases" && record.Kind == "record" {
			databaseRecords = append(databaseRecords, record)
		}
	}
	loadedOrg := core.CloneOrganization(org)
	if err := materializeDatabases(&loadedOrg, databaseRecords); err != nil {
		t.Fatal(err)
	}
	backups := loadedOrg.Projects[project.ID].Databases[databaseID].Backups
	if len(backups) != 1 || backups[0].ID != backupID || backups[0].DatabaseID != databaseID || string(backups[0].Documents["orders"]["order"]) != string(backupDocument) {
		t.Fatalf("fresh backup identity, references or content changed: %+v", backups)
	}
}
