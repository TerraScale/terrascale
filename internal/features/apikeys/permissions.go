package apikeys

import (
	"encoding/json/v2"
	"slices"
	"strings"
)

const (
	readAccess      = "database:read"
	writeAccess     = "database:write"
	readWriteAccess = "database:read,database:write"
)

// accessFor never replaces a rejected or unsupported draft with broader access.
// These options grant only the data permissions supported by the issuer.
func (d pageData) accessFor(kind string) string {
	draft := d.draftFor(kind)
	if draft.Scopes == "" && !(d.Create == kind && d.Error != "") {
		return readAccess
	}
	scopes := formScopes(draft.Scopes)
	for i, scope := range scopes {
		if mapped, ok := terraBaseScopes[scope]; ok {
			scopes[i] = strings.Replace(mapped, "documents:", "database:", 1)
		}
	}
	slices.Sort(scopes)
	scopes = slices.Compact(scopes)
	switch strings.Join(scopes, ",") {
	case readAccess:
		return readAccess
	case writeAccess:
		return writeAccess
	case readWriteAccess:
		return readWriteAccess
	default:
		return ""
	}
}

func (d pageData) databaseFor(kind string) string {
	for _, db := range d.Databases {
		if db.ID == d.draftFor(kind).DatabaseID {
			return db.Name
		}
	}
	return "All project databases (current and future)"
}

func (d pageData) permissionSignals(kind string) string {
	value, _ := json.Marshal(map[string]string{
		"_" + kind + "Access":   d.accessFor(kind),
		"_" + kind + "Database": d.databaseFor(kind),
	})
	return string(value)
}

func permissionSummary(kind string) string {
	return "$_" + kind + "Access === '" + readWriteAccess + "' ? 'Can read, create, update, and delete records.' : $_" + kind + "Access === '" + readAccess + "' ? 'Can read records. Cannot create, update, or delete them.' : $_" + kind + "Access === '" + writeAccess + "' ? 'Can create, update, and delete records. Cannot read or query them.' : 'Choose an access level before creating this credential.'"
}
