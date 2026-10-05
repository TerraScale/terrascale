package sdkrouting_test

import (
	"testing"

	"terrascale.tech/dashboard/internal/adapters/tsgo"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
)

func TestDocumentsSDKUsesConfiguredRegionalOrigin(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	server := terrabasetest.Start(t)
	provisioned, key := terrabasetest.Database(t, server, "dashboard")
	factory := tsgo.Factory{APIBaseURL: server.URL, AccessToken: key, HTTPClient: server.Client}
	client, err := factory.ForDocuments("terrascale-internal", provisioned.Project())
	if err != nil {
		t.Fatal(err)
	}
	db := client.Database(provisioned.Project(), provisioned.Name())
	if _, err := db.CreateCollection(t.Context(), "records"); err != nil {
		t.Fatal(err)
	}
	id := v1.DocumentIDFor("records", "routing-check")
	if _, err := db.Collection("records").Put(t.Context(), id, map[string]string{"source": "dashboard"}, v1.IfNoneMatchAny()); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Collection("records").Get(t.Context(), id); err != nil {
		t.Fatal(err)
	}
	if _, err := (tsgo.Factory{AccessToken: key}).ForDocuments("terrascale-internal", provisioned.Project()); err == nil {
		t.Fatal("missing resolver override accepted")
	}
}
