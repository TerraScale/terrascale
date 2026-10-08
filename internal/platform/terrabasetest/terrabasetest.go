// Package terrabasetest provisions dashboard fixtures on the pinned TerraBase
// release started by the ts-go harness (terrascale.tech/sdk/terrabasetest).
package terrabasetest

import (
	"context"
	"crypto/rand"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
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
	m, err := v1.NewManagement(v1.Config{BaseURL: s.URL, HTTPClient: s.Client})
	if err != nil {
		t.Fatal(err)
	}
	u := m.WithToken(s.Token(t, core.NewID(), ""))
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

// System is a database provisioned as the user of a ts-auth access token,
// as TerraBase's bootstrap provisions each service's database,
// and mirrored into a dashboard account as a platform admin's sign-in does.
type System struct {
	Management *v1.Management
	// Token is the user's ts-auth access token.
	Token     string
	Principal core.Principal
	Database  core.Database
	Documents *v1.Database
}

// SystemDatabase provisions System on s for a new platform admin of store.
func SystemDatabase(t testing.TB, s *Server, store core.Store) System {
	t.Helper()
	ctx := t.Context()
	m, err := v1.NewManagement(v1.Config{BaseURL: s.URL, HTTPClient: s.Client})
	if err != nil {
		t.Fatal(err)
	}
	email := "admin-" + strings.ToLower(rand.Text()) + "@example.test"
	userID := core.NewID()
	token := s.Token(t, userID, email)
	u := m.WithToken(token)
	org, err := u.CreateOrganization(ctx, "terrascale "+rand.Text())
	if err != nil {
		t.Fatal(err)
	}
	project, err := u.CreateProject(ctx, org.ID, "ts-auth")
	if err != nil {
		t.Fatal(err)
	}
	managed, err := u.CreateDatabase(ctx, org.ID, project.ID, "auth")
	if err != nil {
		t.Fatal(err)
	}
	c, err := v1.New(v1.Config{BaseURL: s.URL, Token: s.APIKey(t, org.ID, project.ID, "documents:read", "documents:write"), HTTPClient: s.Client})
	if err != nil {
		t.Fatal(err)
	}
	account, err := store.CreateAccount(ctx, core.User{ID: userID, Name: "Admin", Email: email, PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	db := core.Database{System: true, ID: managed.ID, ProjectID: project.ID, Name: managed.Name, Status: "ready", CreatedAt: managed.CreatedAt}
	err = store.Update(ctx, account.ID, func(a *core.Account) error {
		a.Organizations[org.ID] = core.Organization{
			ID: org.ID, Name: org.Name, Kind: "business", Status: "active", OwnerUserID: a.User.ID, CreatedAt: org.CreatedAt,
			Members: map[string]core.Member{a.User.ID: {UserID: a.User.ID, Email: a.User.Email, Name: a.User.Name, Role: "owner", Status: "active", CreatedAt: org.CreatedAt}},
			Projects: map[string]core.Project{project.ID: {
				ID: project.ID, System: true, OrganizationID: org.ID, Name: project.Name, Status: "active", CreatedAt: project.CreatedAt,
				Databases: map[string]core.Database{db.ID: db}, APIKeys: map[string]core.APIKey{}, SavedQueries: map[string]core.SavedQuery{},
				SharedResultConfigs: map[string]core.SharedResultConfig{}, BackupPolicies: map[string]core.BackupPolicy{},
			}},
			Invitations: map[string]core.Invitation{}, CheckoutSessions: map[string]core.CheckoutSession{},
		}
		a.ActiveOrganizationID, a.ActiveProjectID = org.ID, project.ID
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if account, err = store.FindByEmail(ctx, email); err != nil {
		t.Fatal(err)
	}
	return System{Management: m, Token: token, Principal: account.Principal(), Database: db, Documents: c.Database(project.ID, managed.Name)}
}

// WithToken is ctx carrying the system user's ts-auth access token, as the
// dashboard's session middleware does.
func (s System) WithToken(ctx context.Context) context.Context {
	return core.WithUserToken(ctx, func() (string, bool) { return s.Token, true })
}
