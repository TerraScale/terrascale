// Package terrabasetest provisions dashboard fixtures on the pinned TerraBase
// release started by the ts-go harness (terrascale.tech/sdk/terrabasetest).
package terrabasetest

import (
	"crypto/rand"
	"testing"

	"terrascale.tech/sdk/terrabasetest"
	v1 "terrascale.tech/sdk/v1"
)

// Server is the running pinned TerraBase.
type Server = terrabasetest.Server

// Start runs the pinned TerraBase until the test ends; it skips without the
// executable unless TERRABASE_REQUIRE=1.
func Start(t testing.TB) *Server { return terrabasetest.Start(t) }

// Database provisions a fresh local organization/project fixture, its Documents
// API key and database. Only this isolated test bootstrap uses Management;
// dashboard state operations use the returned Documents handle exclusively.
func Database(t testing.TB, s *Server, name string) (db *v1.Database, key string) {
	t.Helper()
	m, err := v1.NewManagement(v1.Config{BaseURL: s.URL, Token: s.Credential, HTTPClient: s.Client})
	if err != nil {
		t.Fatal(err)
	}
	u := m.As("test-" + rand.Text())
	org, err := u.CreateOrganization(t.Context(), "Fixture "+rand.Text())
	if err != nil {
		t.Fatal(err)
	}
	project, err := u.CreateProject(t.Context(), org.ID, "Dashboard")
	if err != nil {
		t.Fatal(err)
	}
	key = s.APIKey(t, org.ID, project.ID, "documents:read", "documents:write")
	c, err := v1.New(v1.Config{BaseURL: s.URL, Token: key, HTTPClient: s.Client})
	if err != nil {
		t.Fatal(err)
	}
	db = c.Database(project.ID, name)
	if _, err := db.Create(t.Context()); err != nil {
		t.Fatal(err)
	}
	return db, key
}
