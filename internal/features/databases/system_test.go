package databases

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
)

// TestSystemDatabaseCountsComeFromTerraBase covers the Databases page and a
// database's overview: a system database counts the collections, documents
// and bytes TerraBase reports, not its dashboard record.
func TestSystemDatabaseCountsComeFromTerraBase(t *testing.T) {
	tb := terrabasetest.Start(t)
	store := memstore.New()
	system := terrabasetest.SystemDatabase(t, tb, store)
	ctx := t.Context()
	for _, name := range []string{"users", "sessions", "rate_limits"} {
		if _, err := system.Documents.CreateCollection(ctx, name); err != nil {
			t.Fatal(err)
		}
	}
	for _, email := range []string{"ana@example.com", "bia@example.com"} {
		if _, err := system.Documents.Collection("users").Create(ctx, map[string]any{"email": email}); err != nil {
			t.Fatal(err)
		}
	}
	deps := core.Deps{Store: store, Management: system.Management, DocumentsOnly: true}
	p, db := system.Principal, system.Database
	// A stale local collection in the record never counts.
	db.Documents = map[string]map[string][]byte{"test": {"doc": []byte(`{}`)}}

	summary := summarize(system.WithToken(ctx), deps, p, map[string]core.Database{db.ID: db})
	if summary.Collections != 3 || summary.Documents != 2 || summary.Bytes <= 0 || summary.Unavailable != "" {
		t.Fatalf("summary = %+v; want 3 collections, 2 documents and their bytes", summary)
	}
	detail, err := Service{deps: deps}.Detail(system.WithToken(ctx), p, db.ID)
	if err != nil || detail.Collections != 3 || detail.Documents != 2 || detail.CanManage {
		t.Fatalf("detail = %+v, %v; want live counts and no management", detail, err)
	}
	for tab, want := range map[string]bool{"overview": true, "documents": true, "sql": true, "schema": false, "indexes": false, "backups": false, "branches": false, "import": false} {
		if Shows(db, tab) != want {
			t.Errorf("system database shows %s = %v, want %v", tab, !want, want)
		}
	}
	if _, err := (Service{deps: deps}).Branch(system.WithToken(ctx), p, db.ID, ""); err != core.ErrSystemReadOnly {
		t.Fatalf("branch = %v, want read-only", err)
	}
	if summary := summarize(ctx, deps, p, map[string]core.Database{db.ID: db}); summary.Documents != 0 || summary.Unavailable == "" {
		t.Fatalf("summary without a ts-auth token = %+v; want it unavailable", summary)
	}
}

// TestStalledTerraBaseLeavesCountsUnavailable covers a TerraBase that
// accepts the connection and never answers: the page renders within the
// count budget, with the counts unavailable.
func TestStalledTerraBaseLeavesCountsUnavailable(t *testing.T) {
	stalled := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { <-r.Context().Done() }))
	t.Cleanup(stalled.Close)
	management, err := v1.NewManagement(v1.Config{BaseURL: stalled.URL})
	if err != nil {
		t.Fatal(err)
	}
	budget := countBudget
	countBudget = 100 * time.Millisecond
	t.Cleanup(func() { countBudget = budget })
	db := core.Database{ID: "01a1105a-7389-78d7-b95d-45e0966f1408", ProjectID: "01a1105a-7350-7f48-a96e-d71bbb45d7a8", Name: "dashboard"}
	p := core.Principal{UserID: "usr_admin", OrganizationID: "01a11059-a8df-7b4d-b9b9-ac5ba2d25c35", PlatformAdmin: true}
	ctx := core.WithUserToken(t.Context(), func() (string, bool) { return "access-token", true })
	start := time.Now()
	summary := summarize(ctx, core.Deps{Management: management}, p, map[string]core.Database{db.ID: db})
	if elapsed := time.Since(start); elapsed > 5*time.Second || summary.Unavailable == "" {
		t.Fatalf("summary = %+v after %v; want it unavailable within the budget", summary, elapsed)
	}
}
