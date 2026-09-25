package data

import (
	"bytes"
	"context"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestMarioIndexMigrationIsOnlyForSeededOrders(t *testing.T) {
	principal := core.Principal{
		UserID: "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", OrganizationID: "org_01JC9Q5V2N4P6T8R0X2Z4B6D8",
		ProjectID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA",
	}
	account := core.Account{User: core.User{ID: principal.UserID, Email: "mario@terrascale.tech"}}
	database := core.Database{ID: "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", ProjectID: principal.ProjectID, Name: "core-primary"}
	sample := marioIndexMigration(account, principal, database, "orders")
	if sample == nil || sample.State != "running" || sample.Completed != 8_640 || sample.Total != 12_480 || sample.Percent != 69 {
		t.Fatalf("Mario migration sample = %#v", sample)
	}
	otherPrincipal := principal
	otherPrincipal.OrganizationID = "another-organization"
	if marioIndexMigration(account, otherPrincipal, database, "orders") != nil {
		t.Fatal("migration sample leaked to another organization")
	}
	otherDatabase := database
	otherDatabase.ID = "another-database"
	if marioIndexMigration(account, principal, otherDatabase, "orders") != nil || marioIndexMigration(account, principal, database, "users") != nil {
		t.Fatal("migration sample leaked to another database or collection")
	}
	otherAccount := account
	otherAccount.User.Email = "fresh@example.test"
	if marioIndexMigration(otherAccount, principal, database, "orders") != nil {
		t.Fatal("migration sample leaked to a fresh account")
	}
}

func TestMigrationTabShowsStaticSampleOrUnavailableState(t *testing.T) {
	render := func(sample *demoIndexMigration) string {
		t.Helper()
		var output bytes.Buffer
		data := pageData{Mode: "indexes", IndexTab: "migration", Demo: true, DatabaseID: "db-1", Collection: "orders", Migration: sample}
		if err := dataContent(core.Page{}, data).Render(context.Background(), &output); err != nil {
			t.Fatal(err)
		}
		return output.String()
	}
	sample := &demoIndexMigration{State: "running", Completed: 8_640, Total: 12_480, Percent: 69, Watermark: "doc_sample"}
	body := render(sample)
	if !strings.Contains(body, "Static sample from the Mario in-memory demo") || !strings.Contains(body, `value="8640" max="12480"`) || !strings.Contains(body, "doc_sample") {
		t.Fatalf("sample migration visual missing: %s", body)
	}
	if strings.Contains(body, "<button") {
		t.Fatal("static migration sample unexpectedly exposes a mutation control")
	}
	if !strings.Contains(render(nil), "Index migration state is unavailable in the current provider contract") {
		t.Fatal("fresh account did not retain unavailable migration state")
	}
}
