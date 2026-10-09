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
	if principal.UserID != "7CVZQJY80VY06TJG7VP8TQ44DM" ||
		account.User.ID != principal.UserID || account.User.Email != "mario@terrascale.tech" ||
		principal.OrganizationID != "7N1PZ3BD9WCGBVV4V50V508NRX" ||
		principal.ProjectID != "1X1TETYPPH2APBVN5WGY9D3S3G" ||
		database.ID != "0Z793APJ2ECFW2F7P6J9F2RKVY" ||
		database.ProjectID != principal.ProjectID || database.Name != "core-primary" ||
		collection != "orders" {
		return nil
	}
	return &demoIndexMigration{
		State: "running", Completed: 8_640, Total: 12_480, Percent: 69,
		Watermark: "3GH7W4YYGHPZTKFTPXC4QRX5XE",
	}
}
