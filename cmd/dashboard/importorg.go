package main

import (
	"cmp"
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"strings"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/platform/config"
	v1 "terrascale.tech/sdk/v1"
)

// importOrg prepares the configured dashboard database (collections and plan
// catalog) and mirrors one TerraBase Management organization into it. Every
// step is idempotent, so it is safe to rerun.
func importOrg(args []string) error {
	flags := flag.NewFlagSet("import-org", flag.ContinueOnError)
	org := flags.String("org", "", "TerraBase organization ID or name")
	owner := flags.String("owner-email", "", "dashboard owner of the imported organization")
	if err := flags.Parse(args); err != nil {
		return err
	}
	if *org == "" || *owner == "" {
		return errors.New("--org and --owner-email are required")
	}
	cfg, err := config.Load()
	if err != nil {
		return err
	}
	if cfg.TerraBaseURL == "" {
		return errors.New("TERRABASE_URL, TERRABASE_API_KEY, TERRABASE_PROJECT_ID and TERRABASE_DATABASE are required")
	}
	client, err := v1.New(v1.Config{BaseURL: cfg.TerraBaseURL, Token: cfg.TerraBaseAPIKey})
	if err != nil {
		return err
	}
	management, err := v1.NewManagement(v1.Config{
		BaseURL: cmp.Or(strings.TrimSpace(os.Getenv("TERRABASE_MANAGEMENT_URL")), cfg.TerraBaseURL),
		Token:   strings.TrimSpace(os.Getenv("TERRABASE_MANAGEMENT_CREDENTIAL")),
	})
	if err != nil {
		return err
	}
	user := strings.TrimSpace(os.Getenv("TERRABASE_MANAGEMENT_USER_ID"))
	if user == "" {
		return errors.New("TERRABASE_MANAGEMENT_USER_ID is required")
	}
	ctx := context.Background()
	db := client.Database(cfg.TerraBaseProjectID, cfg.TerraBaseDatabase)
	if err := terrabase.EnsureCollections(ctx, db); err != nil {
		return fmt.Errorf("ensure collections: %w", err)
	}
	store := terrabase.New(db)
	if err := store.SeedPlans(ctx); err != nil {
		return fmt.Errorf("seed plans: %w", err)
	}
	imported, err := terrabase.ImportOrganization(ctx, store, management.As(user), *org, *owner)
	if err != nil {
		return err
	}
	fmt.Printf("imported organization %s (%s) with %d projects for %s\n", imported.Name, imported.ID, len(imported.Projects), *owner)
	return nil
}
