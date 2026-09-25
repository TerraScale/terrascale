package e2e

import (
	"context"
	json "encoding/json/v2"
	"errors"
	"slices"
	"strings"
	"testing"
	"uuid"

	"github.com/mxschmitt/playwright-go"

	sdk "terrascale.tech/sdk"
)

func TestAccountCreatedDatabaseWorksThroughIssuedCredentialsAndGoSDK(t *testing.T) {
	if testing.Short() {
		t.Skip("starts a second in-process dashboard with the SDK fixture")
	}
	t.Parallel()
	fixture := startSDKFixture(t)

	page := watchedPage(t, newContext(t, fixture.dashboardURL))
	email := "sdk-e2e-" + uuid.New().String() + "@example.test"
	registerAndOnboard(t, page, credentials{Email: email, Password: "E2e-sdk-journey-123!", DisplayName: "SDK Integration"})

	goTo(t, page, "/databases")
	projectID := requiredAttribute(t, page.Locator("main[data-project-id]"), "data-project-id",
		"onboarding must provision a selected project")
	tenantID := requiredAttribute(t, page.Locator("[data-active-organization-id]"), "data-active-organization-id",
		"the dashboard shell must expose the active organization scope")
	primary := createReadyDatabase(t, page, uniqueName("sdk-e2e"))
	other := createReadyDatabase(t, page, uniqueName("sdk-e2e"))

	session := sessionToken(t, page.Context())
	keyPath := "/api/v1/projects/" + projectID + "/auth/api-keys"
	writeKey := issueAPIKey(t, page, session, keyPath, "SDK fixture writer", primary.ID, "database:read", "database:write")
	readOnlyKey := issueAPIKey(t, page, session, keyPath, "SDK fixture reader", primary.ID, "database:read")
	for _, check := range []struct {
		key    string
		got    []string
		secret string
		want   []string
		dbID   string
	}{
		{"writer", writeKey.APIKey.Scopes, writeKey.Secret, []string{"database:read", "database:write"}, writeKey.APIKey.DatabaseID},
		{"reader", readOnlyKey.APIKey.Scopes, readOnlyKey.Secret, []string{"database:read"}, readOnlyKey.APIKey.DatabaseID},
	} {
		if check.dbID != primary.ID || !slices.Equal(check.got, check.want) {
			t.Errorf("%s key = database %q scopes %v, want database %q scopes %v", check.key, check.dbID, check.got, primary.ID, check.want)
		}
		if strings.HasPrefix(check.secret, "tsk_demo_") {
			t.Errorf("%s key secret %q must come from the producer, not the demo issuer", check.key, check.secret)
		}
	}

	value := sdkDocument{Account: email, Source: "ts-go", DocumentID: "account-" + uuid.New().String()}
	client := func(key string) *sdk.Client {
		t.Helper()
		c, err := sdk.NewClient(sdk.Config{APIBaseURL: fixture.publicAPIURL, AccessToken: key, TenantID: tenantID, ProjectID: projectID})
		must(t, err)
		return c
	}
	writer, reader, invalid := client(writeKey.Secret), client(readOnlyKey.Secret), client("invalid-sdk-fixture-key")
	ctx := t.Context()

	written, err := writer.Documents.Put(ctx, primary.Name, "accounts", value.DocumentID, value, sdk.WithIdempotencyKey(value.DocumentID))
	must(t, err)
	if written.Revision == "" {
		t.Error("put must return a revision")
	}
	expectDocument(t, "writer", writer, primary.Name, value)
	expectSDKStatus(t, "invalid-key get", getDocument(ctx, invalid, primary.Name, value.DocumentID), 401)
	expectDocument(t, "read-only", reader, primary.Name, value)
	_, err = reader.Documents.Put(ctx, primary.Name, "accounts", value.DocumentID+"-blocked", map[string]bool{"blocked": true})
	expectSDKStatus(t, "read-only put", err, 403)
	expectSDKStatus(t, "cross-database get", getDocument(ctx, writer, other.Name, value.DocumentID), 403)
	must(t, writer.Documents.Delete(ctx, primary.Name, "accounts", value.DocumentID, sdk.WithIdempotencyKey(value.DocumentID+"-delete")))
	expectSDKStatus(t, "get after delete", getDocument(ctx, writer, primary.Name, value.DocumentID), 404)
}

type sdkDocument struct {
	Account    string `json:"account"`
	Source     string `json:"source"`
	DocumentID string `json:"documentId"`
}

func getDocument(ctx context.Context, client *sdk.Client, database, id string) error {
	_, err := client.Documents.Get(ctx, database, "accounts", id)
	return err
}

func expectDocument(t *testing.T, name string, client *sdk.Client, database string, want sdkDocument) {
	t.Helper()
	document, err := client.Documents.Get(t.Context(), database, "accounts", want.DocumentID)
	if err != nil {
		t.Errorf("%s get: %v", name, err)
		return
	}
	var got sdkDocument
	if err := json.Unmarshal(document.Body, &got); err != nil || got != want {
		t.Errorf("%s get = %+v (%v), want %+v", name, got, err, want)
	}
}

func expectSDKStatus(t *testing.T, call string, err error, status int) {
	t.Helper()
	if apiErr, ok := errors.AsType[*sdk.APIError](err); !ok || apiErr.Status != status {
		t.Errorf("SDK %s: want HTTP %d, got %v", call, status, err)
	}
}

// createReadyDatabase creates a database from the UI and requires the list
// API to report it ready.
func createReadyDatabase(t *testing.T, page playwright.Page, name string) listedDatabase {
	t.Helper()
	goTo(t, page, "/databases")
	createDatabaseFromUI(t, page, name)
	database := listedDatabaseNamed(t, page, name)
	if database.Status != "ready" {
		t.Fatalf("database %q status = %q, want ready", name, database.Status)
	}
	return database
}
