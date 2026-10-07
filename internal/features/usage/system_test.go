package usage

import (
	"crypto/rand"
	"errors"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	v1 "terrascale.tech/sdk/v1"
)

// TestSystemDatabaseUsageComesFromTerraBase covers the Usage page for a
// project holding a system database: documents, storage and metered writes
// are TerraBase's.
func TestSystemDatabaseUsageComesFromTerraBase(t *testing.T) {
	tb := terrabasetest.Start(t)
	store := memstore.New()
	system := terrabasetest.SystemDatabase(t, tb, store)
	ctx := t.Context()
	if _, err := system.Documents.CreateCollection(ctx, "users"); err != nil {
		t.Fatal(err)
	}
	if _, err := system.Documents.Collection("users").Create(ctx, map[string]any{"email": "ana@example.com"}); err != nil {
		t.Fatal(err)
	}
	service := Service{deps: core.Deps{Store: store, Management: system.Management, DocumentsOnly: true}}
	report, err := service.Usage(system.WithToken(ctx), system.Principal, core.UserToken(system.WithToken(ctx)), "", "")
	if err != nil {
		t.Fatal(err)
	}
	if len(report.Databases) != 1 || report.Databases[0].Documents != 1 || report.Databases[0].LogicalBytes <= 0 {
		t.Fatalf("databases = %+v; want TerraBase's document and storage counts", report.Databases)
	}
	if !report.Totals.Available || report.Totals.Writes < 1 || report.Totals.Error != "" {
		t.Fatalf("totals = %+v; want TerraBase's metered writes", report.Totals)
	}
	writes := report.Totals.Writes

	// A database the dashboard manages in the same project is sample data,
	// never added to TerraBase's metered writes.
	p := system.Principal
	if err := store.UpdateAs(ctx, p, func(a *core.Account) error {
		project := a.Organizations[p.OrganizationID].Projects[p.ProjectID]
		project.Databases["db_local"] = core.Database{ID: "db_local", ProjectID: project.ID, Name: "local", Status: "ready",
			Documents: map[string]map[string][]byte{"items": {"a": []byte(`{}`), "b": []byte(`{}`), "c": []byte(`{}`)}}}
		a.Organizations[p.OrganizationID].Projects[p.ProjectID] = project
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	report, err = service.Usage(system.WithToken(ctx), p, core.UserToken(system.WithToken(ctx)), "", "")
	if err != nil || !report.Totals.Available || report.Totals.Writes != writes {
		t.Fatalf("mixed project totals = %+v, %v; want TerraBase's %d writes only", report.Totals, err, writes)
	}

	// Without a ts-auth token nothing reads as a measured zero.
	report, err = service.Usage(ctx, p, core.UserToken(ctx), "", "")
	if err != nil || report.Totals.Available || report.Totals.Error == "" {
		t.Fatalf("totals without a ts-auth token = %+v, %v; want them unavailable", report.Totals, err)
	}
	for _, database := range report.Databases {
		if database.Unavailable != (database.ID == system.Database.ID) {
			t.Errorf("database %s unavailable = %v", database.Name, database.Unavailable)
		}
	}
	for _, project := range report.Projects {
		if !project.Unavailable {
			t.Errorf("project %s stored bytes look measured without TerraBase", project.Name)
		}
	}
}

// TestSystemDatabaseOfAnotherMember covers a platform admin whose ts-auth
// user is not a member of the system database's TerraBase organization
// (TS-659): TerraBase answers 404, and the pages name the membership instead
// of a timeout.
func TestSystemDatabaseOfAnotherMember(t *testing.T) {
	tb := terrabasetest.Start(t)
	store := memstore.New()
	system := terrabasetest.SystemDatabase(t, tb, store)
	outsider := tb.Token(t, "user-"+rand.Text(), "outsider@example.test")
	ctx := core.WithUserToken(t.Context(), func() (string, bool) { return outsider, true })
	deps := core.Deps{Store: store, Management: system.Management, DocumentsOnly: true}

	_, err := deps.SystemCollections(ctx, system.Principal, system.Database)
	if !errors.Is(err, v1.ErrNotFound) && !errors.Is(err, v1.ErrUnknownRoute) {
		t.Fatalf("collections error = %v; want TerraBase's 404", err)
	}
	if got := core.SystemUnavailable(err); !strings.Contains(got, "add you as a member") {
		t.Fatalf("collections message = %q; want the membership named", got)
	}
	report, err := Service{deps: deps}.Usage(ctx, system.Principal, core.UserToken(ctx), "", "")
	if err != nil {
		t.Fatal(err)
	}
	if report.Totals.Available || !strings.Contains(report.Totals.Error, "add you as a member") {
		t.Fatalf("totals = %+v; want the membership named", report.Totals)
	}
}
