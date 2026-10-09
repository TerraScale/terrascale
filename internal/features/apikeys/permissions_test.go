package apikeys

import "testing"

func TestPermissionDraftDoesNotBroadenAccess(t *testing.T) {
	for _, tc := range []struct {
		name, scopes, error, want string
	}{
		{"new credential", "", "", readAccess},
		{"read only", readAccess, "Invalid name", readAccess},
		{"read and write", readWriteAccess, "Invalid name", readWriteAccess},
		{"unordered scopes", "database:write database:read", "Invalid name", readWriteAccess},
		{"issuer aliases", "documents:read,documents:write", "Invalid name", readWriteAccess},
		{"empty rejected draft", "", "Choose permissions", ""},
		{"unsupported draft", "database:admin", "Choose permissions", ""},
		{"wildcard draft", "*", "Choose permissions", ""},
		{"write only draft", writeAccess, "Invalid name", writeAccess},
		{"write only issuer alias", "documents:write", "Invalid name", writeAccess},
	} {
		t.Run(tc.name, func(t *testing.T) {
			d := pageData{Create: "key", Error: tc.error, Draft: draft{Scopes: tc.scopes}}
			if got := d.accessFor("key"); got != tc.want {
				t.Errorf("access = %q, want %q", got, tc.want)
			}
			if got := d.accessFor("m2m"); got != readAccess {
				t.Errorf("other dialog access = %q, want read only", got)
			}
		})
	}
}

func TestCredentialTypeReflectsActualReadAccess(t *testing.T) {
	for _, tc := range []struct {
		scopes []string
		label  string
	}{
		{[]string{readAccess}, "Read-only key"},
		{[]string{writeAccess}, "Write-only key"},
		{[]string{readAccess, writeAccess}, "Read-write key"},
		{[]string{"documents:write"}, "Write-only key"},
		{[]string{"documents:read", "documents:write"}, "Read-write key"},
		{[]string{"*"}, "Full-access key"},
	} {
		if got := typeOf(kindAPIKey, tc.scopes).Label; got != tc.label {
			t.Errorf("scopes %v have type %q, want %q", tc.scopes, got, tc.label)
		}
	}
}
