// Package dashboardtest runs the dashboard as production does, on the
// pinned TerraBase release: its state in TerraBase, and every database a
// user creates in a TerraBase project made as that user.
package dashboardtest

import (
	"context"
	"crypto/rand"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/adapters/tsgo"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
)

// Dashboard is a signed-in owner of a new Personal organization.
type Dashboard struct {
	Server    *terrabasetest.Server
	Deps      core.Deps
	Principal core.Principal
	// Token is the owner's ts-auth access token.
	Token string
}

// Start runs TerraBase and the dashboard's state on it; it skips without
// the executable unless TERRABASE_REQUIRE=1.
func Start(t testing.TB) Dashboard {
	t.Helper()
	ctx := t.Context()
	s := terrabasetest.Start(t)
	state, _ := terrabasetest.Database(t, s, "dashboard")
	if err := terrabase.EnsureCollections(ctx, state); err != nil {
		t.Fatal(err)
	}
	store := terrabase.New(state)
	if err := store.SeedPlans(ctx); err != nil {
		t.Fatal(err)
	}
	email := "owner-" + strings.ToLower(rand.Text()) + "@example.test"
	account, err := store.CreateAccount(ctx, core.User{Name: "Owner", Email: email, PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	m, err := v1.NewManagement(v1.Config{BaseURL: s.URL, HTTPClient: s.Client})
	if err != nil {
		t.Fatal(err)
	}
	return Dashboard{
		Server: s, Principal: account.Principal(), Token: s.Token(t, "user-"+rand.Text(), email),
		Deps: core.Deps{
			Store: store, SDK: tsgo.Factory{HTTPClient: s.Client}, Management: m,
			Regions: func(context.Context) ([]core.Region, error) {
				return []core.Region{{Code: "GRU", ID: "gru-1", Name: "São Paulo", API: s.URL, Healthy: true}}, nil
			},
		},
	}
}

// WithToken is ctx carrying the owner's ts-auth access token, as the
// dashboard's session middleware does.
func (d Dashboard) WithToken(ctx context.Context) context.Context {
	return core.WithUserToken(ctx, func() (string, bool) { return d.Token, true })
}

// Project reads the owner's selected project.
func (d Dashboard) Project(t testing.TB) core.Project {
	t.Helper()
	a, err := d.Deps.Store.SnapshotAs(t.Context(), d.Principal)
	if err != nil {
		t.Fatal(err)
	}
	project, err := a.ProjectFor(d.Principal, d.Principal.OrganizationID, d.Principal.ProjectID)
	if err != nil {
		t.Fatal(err)
	}
	return project
}

// Management is TerraBase Management acting as the owner.
func (d Dashboard) Management() *v1.ManagementUser { return d.Deps.Management.WithToken(d.Token) }

// TerraBase is the owner's project's TerraBase organization and project, and
// the dashboard's data key there.
func (d Dashboard) TerraBase(t testing.TB) (org, project string, key core.DataKey) {
	t.Helper()
	p := d.Project(t)
	org, project = p.TerraBaseIDs(d.Principal.OrganizationID)
	return org, project, p.DataKeys["gru-1"]
}

// API is a TerraBase Documents client for a data API key, as an
// application holding one uses it.
func (d Dashboard) API(t testing.TB, key string) *v1.Client {
	t.Helper()
	c, err := v1.New(v1.Config{BaseURL: d.Server.URL, Token: key, HTTPClient: d.Server.Client})
	if err != nil {
		t.Fatal(err)
	}
	return c
}
