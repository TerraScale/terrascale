package data

import "terrascale.tech/dashboard/internal/core"

// The migration shown for Mario's seeded orders collection is an immutable
// sample snapshot. It is not derived from provider work or local index builds.
type demoIndexMigration struct {
	State     string `json:"state"`
	Completed int    `json:"completed" jsonschema:"Documents backfilled so far."`
	Total     int    `json:"total"`
	Percent   int    `json:"percent"`
	Watermark string `json:"watermark" jsonschema:"Last backfilled document ID."`
}

func marioIndexMigration(account core.Account, principal core.Principal, database core.Database, collection string) *demoIndexMigration {
	if principal.UserID != "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB" ||
		account.User.ID != principal.UserID || account.User.Email != "mario@terrascale.tech" ||
		principal.OrganizationID != "org_01JC9Q5V2N4P6T8R0X2Z4B6D8" ||
		principal.ProjectID != "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA" ||
		database.ID != "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC" ||
		database.ProjectID != principal.ProjectID || database.Name != "core-primary" ||
		collection != "orders" {
		return nil
	}
	return &demoIndexMigration{
		State: "running", Completed: 8_640, Total: 12_480, Percent: 69,
		Watermark: "doc_01JC9XQ1T3V5X7Z9B2D4F7HJ2",
	}
}
