package terrabasetest_test

import (
	"testing"

	"terrascale.tech/sdk/terrabasetest"
	"terrascale.tech/sdk/v1"
)

func TestDownstreamRestart(t *testing.T) {
	s := terrabasetest.Start(t)
	ctx := t.Context()
	m, err := v1.NewManagement(v1.Config{BaseURL: s.URL, Token: s.Credential, HTTPClient: s.Client})
	if err != nil {
		t.Fatal(err)
	}
	u := m.As("downstream-user")
	org, err := u.CreateOrganization(ctx, "Downstream")
	if err != nil {
		t.Fatal(err)
	}
	project, err := u.CreateProject(ctx, org.ID, "App")
	if err != nil {
		t.Fatal(err)
	}
	cfg := v1.Config{
		BaseURL:    s.URL,
		Token:      s.APIKey(t, org.ID, project.ID, "documents:read", "documents:write"),
		HTTPClient: s.Client,
	}
	c, err := v1.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(project.ID, "main")
	if _, err := db.Create(ctx); err != nil {
		t.Fatal(err)
	}
	if _, err := db.CreateCollection(ctx, "messages"); err != nil {
		t.Fatal(err)
	}
	written, err := db.Collection("messages").Create(ctx, map[string]string{"message": "persisted"})
	if err != nil {
		t.Fatal(err)
	}

	// Restart the downstream app with fresh client/store handles; TerraBase stays running.
	s.Client.CloseIdleConnections()
	c, err = v1.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	db = c.Database(project.ID, "main")
	doc, err := db.Collection("messages").Get(ctx, written.ID)
	if err != nil {
		t.Fatal(err)
	}
	var body map[string]string
	if err := doc.Decode(&body); err != nil {
		t.Fatal(err)
	}
	if len(body) != 1 || body["message"] != "persisted" || doc.Version == "" || doc.Version != written.Version {
		t.Fatalf("after app restart: body=%v, version=%q; want persisted message and version %q", body, doc.Version, written.Version)
	}
}
